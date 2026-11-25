using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ListenTogether.Infrastructure.Networking;
using ListenTogether.Server.Networking;

namespace ListenTogether.Tests.Networking;

public sealed class StunClientTests
{
    [Fact]
    public async Task GetMappedEndPointAsync_ParsesServerReflexiveAddress()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var responder = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync();
            Assert.Equal((ushort)0x0001, BinaryPrimitives.ReadUInt16BigEndian(request.Buffer.AsSpan(0, 2)));
            Assert.True(StunMessageProcessor.TryCreateBindingResponse(
                request.Buffer, request.RemoteEndPoint, out var response));
            await server.SendAsync(response, request.RemoteEndPoint);
        });

        using var clientSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var client = new StunClient();
        var mapped = await client.GetMappedEndPointAsync(
            clientSocket, endpoint, TimeSpan.FromSeconds(2));

        await responder.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(IPAddress.Loopback, mapped.Address);
        Assert.Equal(((IPEndPoint)clientSocket.Client.LocalEndPoint!).Port, mapped.Port);
    }

    [Fact]
    public async Task GetMappedEndPointAsync_IgnoresResponseWithWrongTransactionId()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var responder = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync();
            Assert.True(StunMessageProcessor.TryCreateBindingResponse(
                request.Buffer, request.RemoteEndPoint, out var response));
            response[8] ^= 0xFF;
            await server.SendAsync(response, request.RemoteEndPoint);
        });

        using var clientSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var client = new StunClient();

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetMappedEndPointAsync(
            clientSocket, endpoint, TimeSpan.FromMilliseconds(150)));
        await responder.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetMappedEndPointAsync_ThrowsTimeoutWhenServerDoesNotRespond()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var clientSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var client = new StunClient();

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetMappedEndPointAsync(
            clientSocket,
            (IPEndPoint)server.Client.LocalEndPoint!,
            TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task GetMappedEndPointAsync_HonoursCallerCancellation()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var clientSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var client = new StunClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMappedEndPointAsync(
            clientSocket,
            (IPEndPoint)server.Client.LocalEndPoint!,
            TimeSpan.FromSeconds(5),
            cancellation.Token));
    }
}
