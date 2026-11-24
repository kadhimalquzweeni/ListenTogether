using ListenTogether.Core.Sessions;
using ListenTogether.Infrastructure.Networking;

namespace ListenTogether.Tests.Networking;

public sealed class PeerKeyNegotiationCoordinatorTests
{
    private static readonly Guid SessionId = Guid.Parse("b73c3f38-9179-4c0e-8b3a-7e84dd9d2d50");

    [Fact]
    public async Task BeginPeerAsync_BothPeersCompleteMutualKeyConfirmation()
    {
        var network = new InMemoryPeerNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var listenerSignalling = network.AddPeer("listener");
        using var host = new PeerKeyNegotiationCoordinator(hostSignalling);
        using var listener = new PeerKeyNegotiationCoordinator(listenerSignalling);
        var hostSecured = NewSignal();
        var listenerSecured = NewSignal();

        host.PeerSecured += (peer, _) =>
        {
            if (peer == "listener")
                hostSecured.TrySetResult();
        };
        listener.PeerSecured += (peer, _) =>
        {
            if (peer == "host")
                listenerSecured.TrySetResult();
        };

        await Task.WhenAll(
            host.BeginPeerAsync("listener"),
            listener.BeginPeerAsync("host"));

        await Task.WhenAll(hostSecured.Task, listenerSecured.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("listener", host.SecuredPeers);
        Assert.Contains("host", listener.SecuredPeers);
        Assert.NotNull(host.GetSecuredResult("listener"));
        Assert.NotNull(listener.GetSecuredResult("host"));
        Assert.Equal(
            host.GetSecuredResult("listener")!.SendKey,
            listener.GetSecuredResult("host")!.ReceiveKey);
    }

    [Fact]
    public async Task BeginPeersAsync_EachListenerGetsIndependentHostKeys()
    {
        var network = new InMemoryPeerNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var firstSignalling = network.AddPeer("listener-a");
        var secondSignalling = network.AddPeer("listener-b");
        using var host = new PeerKeyNegotiationCoordinator(hostSignalling);
        using var first = new PeerKeyNegotiationCoordinator(firstSignalling);
        using var second = new PeerKeyNegotiationCoordinator(secondSignalling);
        var secured = new CountdownEvent(4);

        host.PeerSecured += (_, _) => secured.Signal();
        first.PeerSecured += (_, _) => secured.Signal();
        second.PeerSecured += (_, _) => secured.Signal();

        await Task.WhenAll(
            host.BeginPeersAsync(["listener-a", "listener-b"]),
            first.BeginPeerAsync("host"),
            second.BeginPeerAsync("host"));

        Assert.True(secured.Wait(TimeSpan.FromSeconds(5)));
        var hostA = host.GetSecuredResult("listener-a")!;
        var hostB = host.GetSecuredResult("listener-b")!;
        Assert.NotNull(hostA);
        Assert.NotNull(hostB);
        Assert.False(hostA.SendKey.SequenceEqual(hostB.SendKey));
        Assert.False(hostA.TranscriptHash.SequenceEqual(hostB.TranscriptHash));
    }

    [Fact]
    public async Task ProofArrivingBeforeOffer_IsBufferedUntilAgreementCompletes()
    {
        var network = new InMemoryPeerNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var listenerSignalling = network.AddPeer("listener");
        using var host = new PeerKeyNegotiationCoordinator(hostSignalling);
        using var listener = new PeerKeyNegotiationCoordinator(listenerSignalling);
        var secured = NewSignal();

        host.PeerSecured += (_, _) => secured.TrySetResult();

        // Listener sends its offer, then its proof can reach the host before the
        // host's own offer is delivered. The host must retain that proof.
        listenerSignalling.HoldNextOffer = true;
        await listener.BeginPeerAsync("host");
        await host.BeginPeerAsync("listener");
        listenerSignalling.DeliverHeldOffer();

        await secured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("listener", host.SecuredPeers);
    }

    [Fact]
    public async Task ParticipantLeft_DisposesOnlyThatPeersNegotiation()
    {
        var network = new InMemoryPeerNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var firstSignalling = network.AddPeer("listener-a");
        var secondSignalling = network.AddPeer("listener-b");
        using var host = new PeerKeyNegotiationCoordinator(hostSignalling);
        using var first = new PeerKeyNegotiationCoordinator(firstSignalling);
        using var second = new PeerKeyNegotiationCoordinator(secondSignalling);

        await Task.WhenAll(
            host.BeginPeersAsync(["listener-a", "listener-b"]),
            first.BeginPeerAsync("host"),
            second.BeginPeerAsync("host"));

        await WaitUntilAsync(() => host.SecuredPeers.Count == 2);
        var remainingKey = host.GetSecuredResult("listener-b")!.SendKey.ToArray();

        hostSignalling.RaiseParticipantLeft(new SessionParticipant("Listener A", false, "listener-a"));

        Assert.Null(host.GetSecuredResult("listener-a"));
        Assert.Equal(remainingKey, host.GetSecuredResult("listener-b")!.SendKey);
        Assert.Single(host.SecuredPeers);
    }

    [Fact]
    public async Task InvalidProof_DoesNotMarkPeerSecured()
    {
        var network = new InMemoryPeerNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var listenerSignalling = network.AddPeer("listener");
        using var host = new PeerKeyNegotiationCoordinator(hostSignalling);
        using var listener = new PeerKeyNegotiationCoordinator(listenerSignalling);

        listenerSignalling.HoldNextOffer = true;
        await host.BeginPeerAsync("listener");

        hostSignalling.RaiseProof(new PeerKeyProofMessage(
            SessionId, "listener", "host", new byte[32], new byte[32]));

        Assert.Empty(host.SecuredPeers);
        Assert.Null(host.GetSecuredResult("listener"));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected peer negotiation state was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class InMemoryPeerNetwork(Guid sessionId)
    {
        private readonly Dictionary<string, FakePeerSignalling> _peers = new(StringComparer.Ordinal);

        public FakePeerSignalling AddPeer(string connectionId)
        {
            var peer = new FakePeerSignalling(this, sessionId, connectionId);
            _peers.Add(connectionId, peer);
            return peer;
        }

        public Task SendOfferAsync(PeerKeyOffer offer)
        {
            if (!_peers.TryGetValue(offer.ToConnectionId, out var recipient))
                throw new InvalidOperationException("Peer not found.");
            recipient.RaiseOffer(new PeerKeyOfferMessage(offer.FromConnectionId, offer));
            return Task.CompletedTask;
        }

        public Task SendProofAsync(string from, string to, byte[] transcriptHash, byte[] proof)
        {
            if (!_peers.TryGetValue(to, out var recipient))
                throw new InvalidOperationException("Peer not found.");
            recipient.RaiseProof(new PeerKeyProofMessage(sessionId, from, to,
                transcriptHash.ToArray(), proof.ToArray()));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePeerSignalling(
        InMemoryPeerNetwork network,
        Guid sessionId,
        string connectionId) : IPeerKeySignalling
    {
        private PeerKeyOffer? _heldOffer;

        public Guid SessionId => sessionId;
        public string? ConnectionId => connectionId;
        public bool HoldNextOffer { get; set; }

        public event Action<PeerKeyOfferMessage>? PeerKeyOfferReceived;
        public event Action<PeerKeyProofMessage>? PeerKeyProofReceived;
#pragma warning disable CS0067
        public event Action<SessionParticipant>? ParticipantJoined;
        public event Action<SessionParticipant>? ParticipantLeft;
        public event Action<SessionJoinResult>? SessionJoined;
        public event Action<string>? SessionEnded;
        public event Action<Exception?>? ConnectionClosed;
#pragma warning restore CS0067

        public Task PublishPeerKeyOfferAsync(PeerKeyOffer offer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HoldNextOffer)
            {
                HoldNextOffer = false;
                _heldOffer = offer;
                return Task.CompletedTask;
            }
            return network.SendOfferAsync(offer);
        }

        public void DeliverHeldOffer()
        {
            if (_heldOffer is null)
                throw new InvalidOperationException("No offer is being held.");
            var offer = _heldOffer;
            _heldOffer = null;
            _ = network.SendOfferAsync(offer);
        }

        public Task PublishPeerKeyProofAsync(
            string toConnectionId,
            byte[] transcriptHash,
            byte[] proof,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return network.SendProofAsync(connectionId, toConnectionId, transcriptHash, proof);
        }

        public void RaiseOffer(PeerKeyOfferMessage message) => PeerKeyOfferReceived?.Invoke(message);
        public void RaiseProof(PeerKeyProofMessage message) => PeerKeyProofReceived?.Invoke(message);
        public void RaiseParticipantLeft(SessionParticipant participant) => ParticipantLeft?.Invoke(participant);
    }
}
