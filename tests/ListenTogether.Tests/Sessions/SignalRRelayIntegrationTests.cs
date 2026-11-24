using ListenTogether.Core.Sessions;
using ListenTogether.Infrastructure.Networking;
using ListenTogether.Infrastructure.Audio;
using ListenTogether.Core.Audio;
using ListenTogether.Server.Hubs;
using ListenTogether.Server.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace ListenTogether.Tests.Sessions;

public sealed class SignalRRelayIntegrationTests
{
    [Fact]
    public async Task HostAndListener_CanCreateJoinAndRelayAnOpusPacket()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 16 * 1024);

        await using var app = builder.Build();
        app.MapHub<ListeningHub>("/hubs/listening");
        await app.StartAsync();

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>();
        var address = Assert.Single(addresses!.Addresses);
        var serverUri = new Uri(address);

        await using var host = new SignalRSessionClient(serverUri);
        await using var listener = new SignalRSessionClient(serverUri);

        var participantJoined = new TaskCompletionSource<SessionParticipant>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedPacket = new TaskCompletionSource<(byte[] Packet, long Sequence)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionEnded = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        host.ParticipantJoined += participant => participantJoined.TrySetResult(participant);
        listener.AudioPacketReceived += (packet, sequence) =>
            receivedPacket.TrySetResult((packet, sequence));
        listener.SessionEnded += reason => sessionEnded.TrySetResult(reason);

        var invitation = await host.CreateSessionAsync("Host");
        Assert.Equal(8, invitation.Code.Length);

        var joined = await listener.JoinSessionAsync(invitation.Code, "Listener");
        Assert.Equal(invitation.Code, joined.Invitation.Code);
        Assert.Equal("Host", joined.HostDisplayName);
        Assert.Equal(2, joined.Participants.Count);

        var participant = await participantJoined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Listener", participant.DisplayName);

        using var encoder = new OpusAudioEncoder();
        var opusPacket = Assert.Single(encoder.Encode(new AudioFrame(
            new float[OpusAudioEncoder.FrameSamplesPerChannel * AudioFormat.Broadcast.Channels],
            AudioFormat.Broadcast,
            DateTimeOffset.UtcNow)));
        await host.SendAudioPacketAsync(opusPacket, 1);

        var received = await receivedPacket.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(opusPacket, received.Packet);
        Assert.Equal(1, received.Sequence);

        using var decoder = new OpusAudioDecoder();
        var decodedFrame = decoder.Decode(received.Packet);
        Assert.Equal(OpusAudioEncoder.FrameSamplesPerChannel, decodedFrame.FrameCount);

        await host.LeaveSessionAsync();
        var reason = await sessionEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("host", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelayRejectsAudioPublishedByAListener()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSignalR();

        await using var app = builder.Build();
        app.MapHub<ListeningHub>("/hubs/listening");
        await app.StartAsync();

        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        var serverUri = new Uri(address);

        await using var host = new SignalRSessionClient(serverUri);
        await host.CreateSessionAsync("Host");

        await using var listener = new HubConnectionBuilder()
            .WithUrl(new Uri(serverUri, "/hubs/listening"))
            .Build();
        await listener.StartAsync();
        await listener.InvokeAsync("JoinSession", host.SessionCode!, "Listener");

        await Assert.ThrowsAsync<HubException>(() =>
            listener.InvokeAsync("SendAudioPacket", new byte[] { 1, 2, 3 }, 1));
    }

    [Fact]
    public async Task Participants_ExchangePeerCandidatesWithinTheirSession()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSignalR();

        await using var app = builder.Build();
        app.MapHub<ListeningHub>("/hubs/listening");
        await app.StartAsync();

        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        var serverUri = new Uri(address);

        await using var host = new SignalRSessionClient(serverUri);
        await using var listener = new SignalRSessionClient(serverUri);
        var received = new TaskCompletionSource<PeerCandidateMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        listener.PeerCandidateReceived += message => received.TrySetResult(message);

        var invitation = await host.CreateSessionAsync("Host");
        await listener.JoinSessionAsync(invitation.Code, "Listener");

        var candidate = new PeerCandidate("192.168.1.25", 45000, PeerCandidateType.Host);
        await host.PublishPeerCandidateAsync(candidate);

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(candidate, message.Candidate);
        Assert.False(string.IsNullOrWhiteSpace(message.PeerConnectionId));
    }

    [Fact]
    public async Task ServerRejectsInvalidPeerCandidate()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSignalR();

        await using var app = builder.Build();
        app.MapHub<ListeningHub>("/hubs/listening");
        await app.StartAsync();

        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        var serverUri = new Uri(address);

        await using var host = new SignalRSessionClient(serverUri);
        var invitation = await host.CreateSessionAsync("Host");
        var invalid = new PeerCandidate("attacker.example", 45000, PeerCandidateType.Host);

        await Assert.ThrowsAsync<HubException>(() =>
            host.PublishPeerCandidateAsync(invalid));
    }
}
