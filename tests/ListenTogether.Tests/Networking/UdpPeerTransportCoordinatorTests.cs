using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ListenTogether.Core.Sessions;
using ListenTogether.Infrastructure.Networking;

namespace ListenTogether.Tests.Networking;

public sealed class UdpPeerTransportCoordinatorTests
{
    private static readonly Guid SessionId = Guid.Parse("f4e09a4d-9b13-48fd-aec9-7eae1fd54a32");

    [Fact]
    public async Task TwoPeers_EstablishAuthenticatedUdpPathAndExchangeEncryptedAudio()
    {
        var network = new FakeNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var listenerSignalling = network.AddPeer("listener");

        using var hostCandidates = new UdpPeerCandidateCoordinator(hostSignalling, new LoopbackCandidateGatherer());
        using var listenerCandidates = new UdpPeerCandidateCoordinator(listenerSignalling, new LoopbackCandidateGatherer());
        using var hostKeys = new PeerKeyNegotiationCoordinator(hostSignalling);
        using var listenerKeys = new PeerKeyNegotiationCoordinator(listenerSignalling);

        await hostCandidates.StartAsync(new Uri("http://127.0.0.1:5000"));
        await listenerCandidates.StartAsync(new Uri("http://127.0.0.1:5000"));

        var secured = new CountdownEvent(2);
        hostKeys.PeerSecured += (_, _) => secured.Signal();
        listenerKeys.PeerSecured += (_, _) => secured.Signal();

        await Task.WhenAll(
            hostKeys.BeginPeerAsync("listener"),
            listenerKeys.BeginPeerAsync("host"));
        Assert.True(secured.Wait(TimeSpan.FromSeconds(5)));

        await using var hostTransport = new UdpPeerTransportCoordinator(hostCandidates, hostKeys, SessionId);
        await using var listenerTransport = new UdpPeerTransportCoordinator(listenerCandidates, listenerKeys, SessionId);
        var hostConnected = NewSignal();
        var listenerConnected = NewSignal();
        var receivedAudio = new TaskCompletionSource<(byte[] Payload, ulong Sequence)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        hostTransport.PeerConnected += (peer, _) =>
        {
            if (peer == "listener")
                hostConnected.TrySetResult();
        };
        listenerTransport.PeerConnected += (peer, _) =>
        {
            if (peer == "host")
                listenerConnected.TrySetResult();
        };
        listenerTransport.AudioPacketReceived += (peer, payload, sequence) =>
        {
            if (peer == "host")
                receivedAudio.TrySetResult((payload, sequence));
        };

        await Task.WhenAll(hostTransport.StartAsync(), listenerTransport.StartAsync());
        await Task.WhenAll(
            hostConnected.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            listenerConnected.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var opusPayload = new byte[] { 0xF8, 0xFF, 0xFE, 0x10, 0x20, 0x30 };
        var directRecipients = await hostTransport.SendAudioToConnectedPeersAsync(opusPayload, 42);

        Assert.Contains("listener", directRecipients);
        var received = await receivedAudio.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(opusPayload, received.Payload);
        Assert.Equal(42UL, received.Sequence);
        Assert.Contains("listener", hostTransport.ConnectedPeers);
        Assert.Contains("host", listenerTransport.ConnectedPeers);
    }

    [Fact]
    public async Task HostSendsEncryptedAudioDirectlyToMultipleListeners()
    {
        var network = new FakeNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var firstSignalling = network.AddPeer("listener-one");
        var secondSignalling = network.AddPeer("listener-two");
        using var hostCandidates = new UdpPeerCandidateCoordinator(hostSignalling, new LoopbackCandidateGatherer());
        using var firstCandidates = new UdpPeerCandidateCoordinator(firstSignalling, new LoopbackCandidateGatherer());
        using var secondCandidates = new UdpPeerCandidateCoordinator(secondSignalling, new LoopbackCandidateGatherer());
        using var hostKeys = new PeerKeyNegotiationCoordinator(hostSignalling);
        using var firstKeys = new PeerKeyNegotiationCoordinator(firstSignalling);
        using var secondKeys = new PeerKeyNegotiationCoordinator(secondSignalling);
        await Task.WhenAll(
            hostCandidates.StartAsync(new Uri("http://127.0.0.1:5000")),
            firstCandidates.StartAsync(new Uri("http://127.0.0.1:5000")),
            secondCandidates.StartAsync(new Uri("http://127.0.0.1:5000")));

        var secured = new CountdownEvent(4);
        hostKeys.PeerSecured += (_, _) => secured.Signal();
        firstKeys.PeerSecured += (_, _) => secured.Signal();
        secondKeys.PeerSecured += (_, _) => secured.Signal();
        await Task.WhenAll(
            hostKeys.BeginPeerAsync("listener-one"),
            hostKeys.BeginPeerAsync("listener-two"),
            firstKeys.BeginPeerAsync("host"),
            secondKeys.BeginPeerAsync("host"));
        Assert.True(secured.Wait(TimeSpan.FromSeconds(5)));

        await using var hostTransport = new UdpPeerTransportCoordinator(hostCandidates, hostKeys, SessionId);
        await using var firstTransport = new UdpPeerTransportCoordinator(firstCandidates, firstKeys, SessionId);
        await using var secondTransport = new UdpPeerTransportCoordinator(secondCandidates, secondKeys, SessionId);
        var connected = new CountdownEvent(2);
        var firstAudio = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAudio = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostTransport.PeerConnected += (_, _) => connected.Signal();
        firstTransport.AudioPacketReceived += (_, payload, _) => firstAudio.TrySetResult(payload);
        secondTransport.AudioPacketReceived += (_, payload, _) => secondAudio.TrySetResult(payload);
        await Task.WhenAll(hostTransport.StartAsync(), firstTransport.StartAsync(), secondTransport.StartAsync());
        Assert.True(connected.Wait(TimeSpan.FromSeconds(5)));

        var payload = new byte[] { 0xF8, 0xFF, 0xFE, 0x01 };
        var sent = await hostTransport.SendAudioToConnectedPeersAsync(payload, 7);
        Assert.Contains("listener-one", sent);
        Assert.Contains("listener-two", sent);
        Assert.Equal(payload, await firstAudio.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(payload, await secondAudio.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Fact]
    public async Task MissingCandidate_IsReportedAndConnectivityRetriesWhenCandidateArrives()
    {
        var network = new FakeNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var listenerSignalling = network.AddPeer("listener");
        using var hostCandidates = new UdpPeerCandidateCoordinator(hostSignalling, new LoopbackCandidateGatherer());
        using var listenerCandidates = new UdpPeerCandidateCoordinator(listenerSignalling, new LoopbackCandidateGatherer());
        using var hostKeys = new PeerKeyNegotiationCoordinator(hostSignalling);
        using var listenerKeys = new PeerKeyNegotiationCoordinator(listenerSignalling);
        await hostCandidates.StartAsync(new Uri("http://127.0.0.1:5000"));

        var secured = new CountdownEvent(2);
        hostKeys.PeerSecured += (_, _) => secured.Signal();
        listenerKeys.PeerSecured += (_, _) => secured.Signal();
        await Task.WhenAll(hostKeys.BeginPeerAsync("listener"), listenerKeys.BeginPeerAsync("host"));
        Assert.True(secured.Wait(TimeSpan.FromSeconds(5)));

        await using var hostTransport = new UdpPeerTransportCoordinator(hostCandidates, hostKeys, SessionId);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        hostTransport.PeerConnectionFailed += (_, reason) => failed.TrySetResult(reason);
        hostTransport.PeerConnectionAttemptStarted += _ => Interlocked.Increment(ref attempts);
        hostTransport.PeerConnected += (_, _) => reconnected.TrySetResult();
        await hostTransport.StartAsync();

        Assert.Contains("No remote UDP candidates", await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(100); // Allow the failed check round to release its per-peer retry guard.

        await listenerCandidates.StartAsync(new Uri("http://127.0.0.1:5000"));
        await using var listenerTransport = new UdpPeerTransportCoordinator(listenerCandidates, listenerKeys, SessionId);
        await listenerTransport.StartAsync();

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref attempts) >= 2);
        Assert.Contains("listener", hostTransport.ConnectedPeers);
    }
    [Fact]
    public async Task AuthenticatedConnectivityCheck_CanUpdateChangedRemoteEndpoint()
    {
        var network = new FakeNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var listenerSignalling = network.AddPeer("listener");
        using var hostCandidates = new UdpPeerCandidateCoordinator(hostSignalling, new LoopbackCandidateGatherer());
        using var listenerCandidates = new UdpPeerCandidateCoordinator(listenerSignalling, new LoopbackCandidateGatherer());
        using var hostKeys = new PeerKeyNegotiationCoordinator(hostSignalling);
        using var listenerKeys = new PeerKeyNegotiationCoordinator(listenerSignalling);

        await hostCandidates.StartAsync(new Uri("http://127.0.0.1:5000"));
        await listenerCandidates.StartAsync(new Uri("http://127.0.0.1:5000"));
        var secured = new CountdownEvent(2);
        hostKeys.PeerSecured += (_, _) => secured.Signal();
        listenerKeys.PeerSecured += (_, _) => secured.Signal();
        await Task.WhenAll(hostKeys.BeginPeerAsync("listener"), listenerKeys.BeginPeerAsync("host"));
        Assert.True(secured.Wait(TimeSpan.FromSeconds(5)));

        await using var hostTransport = new UdpPeerTransportCoordinator(hostCandidates, hostKeys, SessionId);
        var firstEndpointConnected = new TaskCompletionSource<IPEndPoint>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEndpointConnected = new TaskCompletionSource<IPEndPoint>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await hostTransport.StartAsync();
        hostTransport.PeerConnected += (peer, endpoint) =>
        {
            if (peer != "listener")
                return;
            if (firstEndpointConnected.Task.IsCompleted)
                secondEndpointConnected.TrySetResult(endpoint);
            else
                firstEndpointConnected.TrySetResult(endpoint);
        };

        var listenerKeysResult = listenerKeys.GetSecuredResult("host")!;
        using var listenerSendCodec = new UdpSecurePacketCodec(
            listenerKeysResult.SendKey, listenerKeysResult.SendNoncePrefix);
        using var firstSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var secondSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var hostEndpoint = (IPEndPoint)hostCandidates.LocalCandidateSet!.TransportSocket.Client.LocalEndPoint!;
        var challenge = new byte[16];

        var firstDatagram = listenerSendCodec.Encrypt(
            SessionId, UdpPacketType.ConnectivityCheck, 1, challenge);
        await firstSocket.SendAsync(firstDatagram, hostEndpoint);
        var firstEndpoint = await firstEndpointConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(((IPEndPoint)firstSocket.Client.LocalEndPoint!).Port, firstEndpoint.Port);

        var secondDatagram = listenerSendCodec.Encrypt(
            SessionId, UdpPacketType.ConnectivityCheck, 2, challenge);
        await secondSocket.SendAsync(secondDatagram, hostEndpoint);
        var secondEndpoint = await secondEndpointConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(((IPEndPoint)secondSocket.Client.LocalEndPoint!).Port, secondEndpoint.Port);
        Assert.NotEqual(firstEndpoint.Port, secondEndpoint.Port);
    }

    [Fact]
    public async Task AudioLargerThanSecureDatagramLimit_IsNotSentDirectly()
    {
        var network = new FakeNetwork(SessionId);
        var hostSignalling = network.AddPeer("host");
        var listenerSignalling = network.AddPeer("listener");
        using var hostCandidates = new UdpPeerCandidateCoordinator(hostSignalling, new LoopbackCandidateGatherer());
        using var listenerCandidates = new UdpPeerCandidateCoordinator(listenerSignalling, new LoopbackCandidateGatherer());
        using var hostKeys = new PeerKeyNegotiationCoordinator(hostSignalling);
        using var listenerKeys = new PeerKeyNegotiationCoordinator(listenerSignalling);

        await hostCandidates.StartAsync(new Uri("http://127.0.0.1:5000"));
        await listenerCandidates.StartAsync(new Uri("http://127.0.0.1:5000"));
        var secured = new CountdownEvent(2);
        hostKeys.PeerSecured += (_, _) => secured.Signal();
        listenerKeys.PeerSecured += (_, _) => secured.Signal();
        await Task.WhenAll(hostKeys.BeginPeerAsync("listener"), listenerKeys.BeginPeerAsync("host"));
        Assert.True(secured.Wait(TimeSpan.FromSeconds(5)));

        await using var hostTransport = new UdpPeerTransportCoordinator(hostCandidates, hostKeys, SessionId);
        await hostTransport.StartAsync();

        var oversized = new byte[UdpSecurePacketCodec.MaximumDatagramSize];
        Assert.Empty(await hostTransport.SendAudioToConnectedPeersAsync(oversized, 1));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class LoopbackCandidateGatherer : IUdpCandidateGatherer
    {
        public Task<UdpCandidateSet> GatherAsync(
            IPEndPoint stunServer,
            TimeSpan? stunTimeout = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = (IPEndPoint)socket.Client.LocalEndPoint!;
            var candidate = new PeerCandidate(endpoint.Address.ToString(), endpoint.Port, PeerCandidateType.Host);
            return Task.FromResult(new UdpCandidateSet(socket, [candidate]));
        }
    }

    private sealed class FakeNetwork(Guid sessionId)
    {
        private readonly ConcurrentDictionary<string, FakeSignalling> _peers = new(StringComparer.Ordinal);

        public FakeSignalling AddPeer(string connectionId)
        {
            var peer = new FakeSignalling(this, sessionId, connectionId);
            if (!_peers.TryAdd(connectionId, peer))
                throw new InvalidOperationException("Duplicate fake peer.");
            return peer;
        }

        public Task BroadcastCandidateAsync(string sender, PeerCandidate candidate)
        {
            foreach (var peer in _peers.Values.Where(peer => peer.ConnectionId != sender))
                peer.RaiseCandidate(new PeerCandidateMessage(sender, candidate));
            return Task.CompletedTask;
        }

        public Task SendOfferAsync(PeerKeyOffer offer)
        {
            if (!_peers.TryGetValue(offer.ToConnectionId, out var peer))
                throw new InvalidOperationException("Peer not found.");
            peer.RaiseOffer(new PeerKeyOfferMessage(offer.FromConnectionId, offer));
            return Task.CompletedTask;
        }

        public Task SendProofAsync(string from, string to, byte[] hash, byte[] proof)
        {
            if (!_peers.TryGetValue(to, out var peer))
                throw new InvalidOperationException("Peer not found.");
            peer.RaiseProof(new PeerKeyProofMessage(sessionId, from, to, hash.ToArray(), proof.ToArray()));
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

        public event Action<PeerCandidateMessage>? PeerCandidateReceived;
        public event Action<PeerKeyOfferMessage>? PeerKeyOfferReceived;
        public event Action<PeerKeyProofMessage>? PeerKeyProofReceived;
#pragma warning disable CS0067
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

        public Task PublishPeerKeyOfferAsync(PeerKeyOffer offer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return network.SendOfferAsync(offer);
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

        public void RaiseCandidate(PeerCandidateMessage message) => PeerCandidateReceived?.Invoke(message);
        public void RaiseOffer(PeerKeyOfferMessage message) => PeerKeyOfferReceived?.Invoke(message);
        public void RaiseProof(PeerKeyProofMessage message) => PeerKeyProofReceived?.Invoke(message);
    }
}
