using System.Net;
using System.Net.Sockets;
using ListenTogether.Infrastructure.Networking;

namespace ListenTogether.App.Networking;

public interface IHostReadinessChecker
{
    Task<HostReadinessResult> CheckAsync(Uri serverUri, CancellationToken cancellationToken = default);
}

public interface IStunEndpointProbe
{
    Task CheckAsync(string host, int port, CancellationToken cancellationToken = default);
}

public sealed record HostReadinessResult(bool CanHost, string Message);

/// <summary>
/// Checks signalling health and UDP STUN reachability from this PC. It cannot
/// prove that the router forwards inbound traffic from the public internet.
/// </summary>
public sealed class HostReadinessChecker : IHostReadinessChecker
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;
    private readonly IStunEndpointProbe _stunProbe;

    public HostReadinessChecker(HttpClient? httpClient = null, IStunEndpointProbe? stunProbe = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _stunProbe = stunProbe ?? new StunEndpointProbe();
    }

    public async Task<HostReadinessResult> CheckAsync(Uri serverUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        if (serverUri.Scheme is not ("http" or "https"))
            return new HostReadinessResult(false, "The server address must use HTTP or HTTPS.");

        using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        healthTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _httpClient.GetAsync(
                new Uri(serverUri, "/healthz"), HttpCompletionOption.ResponseHeadersRead, healthTimeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new HostReadinessResult(false,
                    $"The ListenTogether server health check failed (HTTP {(int)response.StatusCode}). Start the server and check its address.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HostReadinessResult(false, "The server health check timed out. Check that the server is running and the address is correct.");
        }
        catch (HttpRequestException exception)
        {
            return new HostReadinessResult(false, $"Couldn't reach the ListenTogether server: {exception.Message}");
        }

        try
        {
            await _stunProbe.CheckAsync(serverUri.Host, 3478, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HostReadinessResult(false,
                "The server responds, but UDP STUN on port 3478 did not respond. Check the STUN service, Windows Firewall, and router UDP 3478 forwarding.");
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or InvalidOperationException)
        {
            return new HostReadinessResult(false,
                $"The server responds, but UDP STUN on port 3478 is unreachable: {exception.Message} Check the STUN service, Windows Firewall, and router UDP 3478 forwarding.");
        }

        return new HostReadinessResult(true,
            "Server health and UDP STUN checks passed from this PC. Verify TCP 80/443 and UDP 3478 from a different internet connection before relying on remote hosting; this local check cannot confirm external router port forwarding.");
    }
}

public sealed class StunEndpointProbe(StunClient? stunClient = null) : IStunEndpointProbe
{
    private readonly StunClient _stunClient = stunClient ?? new StunClient();

    public async Task CheckAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
        if (address is null)
            throw new SocketException((int)SocketError.AddressFamilyNotSupported);

        using var socket = new UdpClient(AddressFamily.InterNetwork);
        await _stunClient.GetMappedEndPointAsync(
            socket, new IPEndPoint(address, port), TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
    }
}
