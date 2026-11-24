using System.Net;
using System.Net.Sockets;
using ListenTogether.Core.Sessions;
using ListenTogether.Infrastructure.Networking;

namespace ListenTogether.Tests.Networking;

public sealed class UdpPeerCandidateCoordinatorTests
{
    private static readonly Guid SessionId = Guid.Parse("a30a0c9f-0a62-40d7-aed5-4e53c0c3a03a");

    [Fact]
    public async Task StartAsync_GathersAndExchangesCandidatesWithOtherParticipants()
    {
        var network = new FakeNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var listenerSignalling = network.AddPeer("listener");
        var hostCandidates = new[]
        {
            new PeerCandidate("192.168.1.10", 51000, PeerCandidateType.Host),
            new PeerCandidate("203.0.113.10", 62000, PeerCandidateType.ServerReflexive)
        };
        var listenerCandidates = new[]
        {
            new PeerCandidate("192.168.1.20", 52000, PeerCandidateType.Host),
            new PeerCandidate("203.0.113.20", 63000, PeerCandidateType.ServerReflexive)
        };

        using var host = new UdpPeerCandidateCoordinator(hostSignalling, new FakeGatherer(hostCandidates));
        using var listener = new UdpPeerCandidateCoordinator(listenerSignalling, new FakeGatherer(listenerCandidates));

        await host.StartAsync(new Uri("http://127.0.0.1:5000"));
        await listener.StartAsync(new Uri("http://127.0.0.1:5000"));

        Assert.Equal(hostCandidates.OrderBy(candidate => candidate.Type).ThenBy(candidate => candidate.Address),
            host.LocalCandidates.OrderBy(candidate => candidate.Type).ThenBy(candidate => candidate.Address));
        Assert.Equal(listenerCandidates.OrderBy(candidate => candidate.Type).ThenBy(candidate => candidate.Address),
            listener.LocalCandidates.OrderBy(candidate => candidate.Type).ThenBy(candidate => candidate.Address));
        Assert.Equal(hostCandidates.OrderBy(candidate => candidate.Type).ThenBy(candidate => candidate.Address),
            listener.GetRemoteCandidates("host").OrderBy(candidate => candidate.Type).ThenBy(candidate => candidate.Address));
        Assert.Equal(listenerCandidates.OrderBy(candidate => candidate.Type).ThenBy(candidate => candidate.Address),
            host.GetRemoteCandidates("listener").OrderBy(candidate => candidate.Type).ThenBy(candidate => candidate.Address));
        Assert.NotNull(host.LocalCandidateSet);
        Assert.NotNull(host.LocalCandidateSet!.TransportSocket.Client.LocalEndPoint);
    }

    [Fact]
    public async Task ParticipantJoined_ExistingPeerRepublishesCandidates()
    {
        var network = new FakeNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        using var host = new UdpPeerCandidateCoordinator(
            hostSignalling,
            new FakeGatherer([new PeerCandidate("192.168.1.10", 51000, PeerCandidateType.Host)]));

        await host.StartAsync(new Uri("http://127.0.0.1:5000"));
        var lateListenerSignalling = network.AddPeer("late-listener");
        using var lateListener = new UdpPeerCandidateCoordinator(
            lateListenerSignalling,
            new FakeGatherer([new PeerCandidate("192.168.1.20", 52000, PeerCandidateType.Host)]));

        hostSignalling.RaiseParticipantJoined(
            new SessionParticipant("Late listener", false, "late-listener"));

        await WaitUntilAsync(() => lateListener.GetRemoteCandidates("host").Count == 1);
        Assert.Single(lateListener.GetRemoteCandidates("host"));
    }

    [Fact]
    public async Task InvalidCandidate_IsIgnoredAndParticipantDepartureClearsCandidates()
    {
        var network = new FakeNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var listenerSignalling = network.AddPeer("listener");
        using var host = new UdpPeerCandidateCoordinator(
            hostSignalling,
            new FakeGatherer([new PeerCandidate("192.168.1.10", 51000, PeerCandidateType.Host)]));
        using var listener = new UdpPeerCandidateCoordinator(
            listenerSignalling,
            new FakeGatherer([new PeerCandidate("192.168.1.20", 52000, PeerCandidateType.Host)]));

        await host.StartAsync(new Uri("http://127.0.0.1:5000"));
        await listener.StartAsync(new Uri("http://127.0.0.1:5000"));

        hostSignalling.RaiseCandidate(new PeerCandidateMessage(
            "listener", new PeerCandidate("not-an-ip", 0, PeerCandidateType.Host)));
        Assert.Single(host.GetRemoteCandidates("listener"));

        hostSignalling.RaiseParticipantLeft(
            new SessionParticipant("Listener", false, "listener"));
        Assert.Empty(host.GetRemoteCandidates("listener"));
    }

    [Fact]
    public async Task SessionEnd_ClearsRemoteCandidatesAndDisposeClosesLocalSocket()
    {
        var network = new FakeNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var listenerSignalling = network.AddPeer("listener");
        var host = new UdpPeerCandidateCoordinator(
            hostSignalling,
            new FakeGatherer([new PeerCandidate("192.168.1.10", 51000, PeerCandidateType.Host)]));
        using var listener = new UdpPeerCandidateCoordinator(
            listenerSignalling,
            new FakeGatherer([new PeerCandidate("192.168.1.20", 52000, PeerCandidateType.Host)]));

        await host.StartAsync(new Uri("http://127.0.0.1:5000"));
        await listener.StartAsync(new Uri("http://127.0.0.1:5000"));
        var socket = host.LocalCandidateSet!.TransportSocket;

        hostSignalling.RaiseSessionEnded("Ended");
        Assert.Empty(host.GetRemoteCandidates("listener"));

        host.Dispose();
        Assert.Null(socket.Client);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected candidate exchange state was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeGatherer(IReadOnlyList<PeerCandidate> candidates) : IUdpCandidateGatherer
    {
        public Task<UdpCandidateSet> GatherAsync(
            IPEndPoint stunServer,
            TimeSpan? stunTimeout = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return Task.FromResult(new UdpCandidateSet(socket, candidates));
        }
    }

    private sealed class FakeNetwork(Guid sessionId)
    {
        private readonly Dictionary<string, FakeSignalling> _peers = new(StringComparer.Ordinal);

        public FakeSignalling AddPeer(string connectionId)
        {
            var peer = new FakeSignalling(this, sessionId, connectionId);
            _peers.Add(connectionId, peer);
            return peer;
        }

        public Task BroadcastCandidateAsync(string sender, PeerCandidate candidate)
        {
            foreach (var peer in _peers.Values.Where(peer => peer.ConnectionId != sender))
                peer.RaiseCandidate(new PeerCandidateMessage(sender, candidate));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSignalling(
        FakeNetwork network,
        Guid sessionId,
        string connectionId) : IPeerKeySignalling
    {
        public Guid SessionId => sessionId;
        public string? ConnectionId => connectionId;

#pragma warning disable CS0067
        public event Action<PeerCandidateMessage>? PeerCandidateReceived;
        public event Action<PeerKeyOfferMessage>? PeerKeyOfferReceived;
        public event Action<PeerKeyProofMessage>? PeerKeyProofReceived;
        public event Action<SessionParticipant>? ParticipantJoined;
        public event Action<SessionParticipant>? ParticipantLeft;
        public event Action<SessionJoinResult>? SessionJoined;
        public event Action<string>? SessionEnded;
        public event Action<Exception?>? ConnectionClosed;
#pragma warning restore CS0067

        public Task PublishPeerCandidateAsync(PeerCandidate candidate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return network.BroadcastCandidateAsync(connectionId, candidate);
        }

        public Task PublishPeerKeyOfferAsync(PeerKeyOffer offer, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PublishPeerKeyProofAsync(
            string toConnectionId,
            byte[] transcriptHash,
            byte[] proof,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RaiseCandidate(PeerCandidateMessage message) => PeerCandidateReceived?.Invoke(message);
        public void RaiseParticipantJoined(SessionParticipant participant) => ParticipantJoined?.Invoke(participant);
        public void RaiseParticipantLeft(SessionParticipant participant) => ParticipantLeft?.Invoke(participant);
        public void RaiseSessionEnded(string reason) => SessionEnded?.Invoke(reason);
    }
}
