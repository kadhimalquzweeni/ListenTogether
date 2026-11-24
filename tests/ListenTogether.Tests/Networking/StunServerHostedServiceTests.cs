using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ListenTogether.Server.Networking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ListenTogether.Tests.Networking;

public sealed class StunServerHostedServiceTests
{
    [Fact]
    public async Task HostedService_AnswersBindingRequestOverUdp()
    {
        int port;
        using (var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;

        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.Configure<StunServerOptions>(options =>
                {
                    options.Enabled = true;
                    options.ListenAddress = IPAddress.Loopback.ToString();
                    options.Port = port;
                });
                services.AddHostedService<StunServerHostedService>();
            })
            .Build();

        await host.StartAsync();

        try
        {
            using var client = new UdpClient(AddressFamily.InterNetwork);
            var request = CreateBindingRequest();
            await client.SendAsync(request, new IPEndPoint(IPAddress.Loopback, port));

            var received = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(StunMessageProcessor.BindingSuccessResponse,
                BinaryPrimitives.ReadUInt16BigEndian(received.Buffer.AsSpan(0, 2)));
            Assert.Equal(32, received.Buffer.Length);
            Assert.Equal((ushort)0x0020,
                BinaryPrimitives.ReadUInt16BigEndian(received.Buffer.AsSpan(20, 2)));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static byte[] CreateBindingRequest()
    {
        var request = new byte[StunMessageProcessor.HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), StunMessageProcessor.BindingRequest);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), StunMessageProcessor.MagicCookie);
        return request;
    }
}
