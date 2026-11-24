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

    /// <summary>
    /// Exchanges UDP endpoint candidates with other participants in this session.
    /// This is signalling only: media continues to use the existing relay until
    /// the direct transport has completed its handshake and fallback implementation.
    /// </summary>
    public async Task PublishPeerCandidate(PeerCandidate candidate)
    {
        if (candidate is null || !candidate.IsValid)
            throw new HubException("The peer candidate is invalid.");

        if (!sessions.TryGetSessionCode(Context.ConnectionId, out var code))
            throw new HubException("Join a listening session before publishing peer candidates.");

        await Clients.OthersInGroup(code).SendAsync(
            "PeerCandidateReceived",
            new PeerCandidateMessage(Context.ConnectionId, candidate),
            Context.ConnectionAborted);
    }

    /// <summary>Returns the server-assigned SignalR identity used in ECDH transcript binding.</summary>
    public string GetConnectionId() => Context.ConnectionId;

    /// <summary>
    /// Relays an ECDH offer to exactly one peer after validating that both
    /// connections are active members of the stated session.
    /// </summary>
    public async Task PublishPeerKeyOffer(PeerKeyOffer offer)
    {
        if (offer is null ||
            offer.PublicKey is null || offer.PublicKey.Length is < 64 or > 512 ||
            offer.Nonce is null || offer.Nonce.Length != 32 ||
            !string.Equals(offer.FromConnectionId, Context.ConnectionId, StringComparison.Ordinal) ||
            !sessions.TryGetPeerSession(offer.SessionId, Context.ConnectionId, offer.ToConnectionId))
            throw new HubException("The peer key offer is invalid or the target is not in this session.");

        await Clients.Client(offer.ToConnectionId).SendAsync(
            "PeerKeyOfferReceived",
            new PeerKeyOfferMessage(Context.ConnectionId, offer),
            Context.ConnectionAborted);
    }

    /// <summary>Relays key confirmation only to the peer bound into the ECDH transcript.</summary>
    public async Task PublishPeerKeyProof(
        Guid sessionId,
        string toConnectionId,
        byte[] transcriptHash,
        byte[] proof)
    {
        if (transcriptHash is null || transcriptHash.Length != 32 ||
            proof is null || proof.Length != 32 ||
            !sessions.TryGetPeerSession(sessionId, Context.ConnectionId, toConnectionId))
            throw new HubException("The peer key confirmation is invalid or the target is not in this session.");

        await Clients.Client(toConnectionId).SendAsync(
            "PeerKeyProofReceived",
            new PeerKeyProofMessage(sessionId, Context.ConnectionId, toConnectionId,
                transcriptHash, proof),
            Context.ConnectionAborted);
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
