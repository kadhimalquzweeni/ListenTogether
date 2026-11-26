using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace ListenTogether.Server.Networking;

/// <summary>
/// Runs a small UDP STUN Binding server alongside the SignalR relay.
/// Only STUN Binding requests are answered; this is not a TURN server.
/// </summary>
public sealed class StunServerHostedService(
    IOptions<StunServerOptions> options,
    ILogger<StunServerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("STUN server is disabled.");
            return;
        }

        var address = IPAddress.Parse(settings.ListenAddress);
        using var udpClient = new UdpClient(new IPEndPoint(address, settings.Port));
        logger.LogInformation(
            "ListenTogether STUN server listening on {Address}:{Port}/UDP.",
            address,
            settings.Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await udpClient.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (!StunMessageProcessor.TryCreateBindingResponse(
                    datagram.Buffer,
                    datagram.RemoteEndPoint,
                    out var response))
            {
                continue;
            }

            try
            {
                await udpClient.SendAsync(response, datagram.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException exception)
            {
                logger.LogDebug(
                    exception,
                    "Could not send a STUN response to {RemoteEndPoint}.",
                    datagram.RemoteEndPoint);
            }
        }
    }
}
