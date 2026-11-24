using System.Net;
using System.Net.Sockets;
using ListenTogether.Infrastructure.Networking;
using ListenTogether.Server.Networking;

namespace ListenTogether.Tests.Networking;

public sealed class UdpCandidateGathererTests
{
    [Fact]
    public async Task GatherAsync_ReturnsCandidatesForTheSameBoundSocket()
    {
        using var stunServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var stunEndpoint = (IPEndPoint)stunServer.Client.LocalEndPoint!;
        var responder = Task.Run(async () =>
        {
            var request = await stunServer.ReceiveAsync();
            Assert.True(StunMessageProcessor.TryCreateBindingResponse(
                request.Buffer, request.RemoteEndPoint, out var response));
            await stunServer.SendAsync(response, request.RemoteEndPoint);
        });

        var gatherer = new UdpCandidateGatherer();
        using var result = await gatherer.GatherAsync(
            stunEndpoint, TimeSpan.FromSeconds(2));

        await responder.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEmpty(result.Candidates);
        Assert.Contains(result.Candidates, candidate =>
            candidate.Type == ListenTogether.Core.Sessions.PeerCandidateType.ServerReflexive &&
            candidate.Address == IPAddress.Loopback.ToString());

        var socketPort = ((IPEndPoint)result.TransportSocket.Client.LocalEndPoint!).Port;
        Assert.All(result.Candidates, candidate => Assert.Equal(socketPort, candidate.Port));
        Assert.All(result.Candidates, candidate => Assert.True(candidate.IsValid));
    }

    [Fact]
    public async Task GatherAsync_RejectsIpv6StunEndpointUntilIpv6ListenerIsConfigured()
    {
        using var socket = new UdpClient(AddressFamily.InterNetworkV6);
        var gatherer = new UdpCandidateGatherer();

        await Assert.ThrowsAsync<NotSupportedException>(() => gatherer.GatherAsync(
            new IPEndPoint(IPAddress.IPv6Loopback, 3478)));
    }

    [Fact]
    public async Task GatherAsync_KeepsLocalCandidatesWhenStunRequestTimesOut()
    {
        using var silentServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var gatherer = new UdpCandidateGatherer();

        using var result = await gatherer.GatherAsync(
            (IPEndPoint)silentServer.Client.LocalEndPoint!,
            TimeSpan.FromMilliseconds(100));

        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, candidate =>
            Assert.Equal(ListenTogether.Core.Sessions.PeerCandidateType.Host, candidate.Type));
        Assert.NotNull(result.TransportSocket.Client.LocalEndPoint);
    }
}
