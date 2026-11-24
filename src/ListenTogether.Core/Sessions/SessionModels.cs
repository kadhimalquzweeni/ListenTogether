namespace ListenTogether.Core.Sessions;

public enum SessionRole
{
    Host,
    Listener
}

/// <summary>A session invitation carries the stable identifier used by peer key agreement.</summary>
public sealed record SessionInvitation(string Code, Uri? JoinUri = null, Guid SessionId = default);

public sealed record SessionParticipant(string DisplayName, bool IsHost);

public sealed record SessionJoinResult(
    SessionInvitation Invitation,
    string HostDisplayName,
    IReadOnlyList<SessionParticipant> Participants,
    SessionParticipant JoinedParticipant);

/// <summary>Ephemeral ECDH public-key offer. Kept in Core so it can cross the signalling boundary.</summary>
public sealed record PeerKeyOffer(
    Guid SessionId,
    string FromConnectionId,
    string ToConnectionId,
    byte[] PublicKey,
    byte[] Nonce);

/// <summary>Ephemeral ECDH public-key offer routed only to the named session peer.</summary>
public sealed record PeerKeyOfferMessage(string PeerConnectionId, PeerKeyOffer Offer);

/// <summary>Transcript-bound key confirmation proof routed only to the named session peer.</summary>
public sealed record PeerKeyProofMessage(
    Guid SessionId,
    string FromConnectionId,
    string ToConnectionId,
    byte[] TranscriptHash,
    byte[] Proof);
