using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ListenTogether.Core.Sessions;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// Owns the UDP socket whose local and server-reflexive candidates were gathered.
/// Keep this socket alive and use it for subsequent connectivity checks/media;
/// creating a new socket would invalidate the gathered NAT mapping.
/// </summary>
public sealed class UdpCandidateSet : IDisposable
{
    private bool _disposed;

    public UdpCandidateSet(UdpClient socket, IReadOnlyList<PeerCandidate> candidates)
    {
        TransportSocket = socket ?? throw new ArgumentNullException(nameof(socket));
        Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        if (Candidates.Count == 0 || Candidates.Any(candidate => candidate is null || !candidate.IsValid))
            throw new ArgumentException("At least one valid UDP candidate is required.", nameof(candidates));
    }

    public UdpClient TransportSocket { get; }
    public IReadOnlyList<PeerCandidate> Candidates { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        TransportSocket.Dispose();
    }
}

/// <summary>
/// Binds one reusable UDP socket, gathers active local IPv4 addresses, and
/// asks the configured STUN endpoint for the socket's public mapped endpoint.
/// </summary>
public sealed class UdpCandidateGatherer(StunClient? stunClient = null) : IUdpCandidateGatherer
{
    private readonly StunClient _stunClient = stunClient ?? new StunClient();

    public async Task<UdpCandidateSet> GatherAsync(
        IPEndPoint stunServer,
        TimeSpan? stunTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stunServer);
        if (stunServer.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException("The current self-hosted STUN endpoint is configured for IPv4.");

        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var localPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            var candidates = GetLocalIpv4Addresses()
                .Select(address => new PeerCandidate(address.ToString(), localPort, PeerCandidateType.Host))
                .Distinct()
                .ToList();

            IPEndPoint mapped;
            try
            {
                mapped = await _stunClient.GetMappedEndPointAsync(
                    socket,
                    stunServer,
                    stunTimeout ?? TimeSpan.FromSeconds(3),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (candidates.Count > 0)
            {
                // Host candidates remain useful for same-LAN peers even when
                // the STUN endpoint is blocked or temporarily unavailable.
                return new UdpCandidateSet(socket, candidates.AsReadOnly());
            }

            var reflexive = new PeerCandidate(
                mapped.Address.ToString(),
                mapped.Port,
                PeerCandidateType.ServerReflexive);

            if (!candidates.Contains(reflexive))
                candidates.Add(reflexive);

            return new UdpCandidateSet(socket, candidates.AsReadOnly());
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static IEnumerable<IPAddress> GetLocalIpv4Addresses()
    {
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up ||
                network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            IPInterfaceProperties properties;
            try { properties = network.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork ||
                    IPAddress.IsLoopback(address) ||
                    address.Equals(IPAddress.Any) ||
                    address.GetAddressBytes()[0] == 169 && address.GetAddressBytes()[1] == 254)
                    continue;

                yield return address;
            }
        }
    }
}
