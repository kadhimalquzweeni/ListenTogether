using ListenTogether.Core.Sessions;
using ListenTogether.Server.Sessions;
using Microsoft.AspNetCore.SignalR;

namespace ListenTogether.Server.Hubs;

public sealed class ListeningHub(SessionRegistry sessions) : Hub
{
    public async Task<SessionInvitation> CreateSession(string displayName)
    {
        try
        {
            var invitation = sessions.CreateSession(Context.ConnectionId, displayName);
            try
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, invitation.Code, Context.ConnectionAborted);
                return invitation;
            }
            catch
            {
                sessions.RemoveConnection(Context.ConnectionId);
                throw;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new HubException(exception.Message);
        }
    }

    public async Task<SessionJoinResult> JoinSession(string code, string displayName)
    {
        try
        {
            var result = sessions.JoinSession(Context.ConnectionId, code, displayName);
            try
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, result.Invitation.Code, Context.ConnectionAborted);
                await Clients.OthersInGroup(result.Invitation.Code)
                    .SendAsync("ParticipantJoined", result.JoinedParticipant, Context.ConnectionAborted);
                return result;
            }
            catch
            {
                sessions.RemoveConnection(Context.ConnectionId);
                throw;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new HubException(exception.Message);
        }
    }

    public async Task SendAudioPacket(byte[] packet, long sequence)
    {
        if (packet is null || !sessions.TryAcceptAudioPacket(Context.ConnectionId, sequence, packet.Length))
            throw new HubException("This connection cannot publish that audio packet.");

        if (sessions.TryGetSessionCode(Context.ConnectionId, out var code))
        {
            await Clients.OthersInGroup(code)
                .SendAsync("ReceiveAudioPacket", packet, sequence, Context.ConnectionAborted);
        }
    }

    public async Task SendAudioPacketBatch(byte[][] packets, long[] sequences)
    {
        if (packets is null || sequences is null ||
            !sessions.TryAcceptAudioPacketBatch(
                Context.ConnectionId,
                sequences,
                packets.Select(packet => packet?.Length ?? 0).ToArray()))
            throw new HubException("This connection cannot publish that audio batch.");

        if (!sessions.TryGetSessionCode(Context.ConnectionId, out var code))
            throw new HubException("This connection is not in a listening session.");

        for (var index = 0; index < packets.Length; index++)
        {
            await Clients.OthersInGroup(code).SendAsync(
                "ReceiveAudioPacket", packets[index], sequences[index], Context.ConnectionAborted);
        }
    }

    public async Task LeaveSession()
    {
        var departure = sessions.RemoveConnection(Context.ConnectionId);
        if (departure is null)
            return;

        if (departure.IsHost)
        {
            await Clients.Group(departure.Code).SendAsync(
                "SessionEnded", "The host ended the listening session.");
        }
        else
        {
            await Clients.OthersInGroup(departure.Code)
                .SendAsync("ParticipantLeft", departure.Participant);
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, departure.Code, Context.ConnectionAborted);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var departure = sessions.RemoveConnection(Context.ConnectionId);
        if (departure is not null)
        {
            if (departure.IsHost)
            {
                await Clients.Group(departure.Code).SendAsync(
                    "SessionEnded", "The host disconnected.");
            }
            else
            {
                await Clients.OthersInGroup(departure.Code)
                    .SendAsync("ParticipantLeft", departure.Participant);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}
