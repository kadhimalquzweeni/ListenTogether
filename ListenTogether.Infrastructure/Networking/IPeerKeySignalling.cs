using ListenTogether.Core.Sessions;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// Minimal signalling contract consumed by peer key negotiation. Separating this
/// from the SignalR implementation makes the security protocol independently testable.
/// </summary>
public interface IPeerKeySignalling
{
    Guid SessionId { get; }
    string? ConnectionId { get; }

    event Action<PeerCandidateMessage>? PeerCandidateReceived;
    event Action<PeerKeyOfferMessage>? PeerKeyOfferReceived;
    event Action<PeerKeyProofMessage>? PeerKeyProofReceived;
    event Action<SessionParticipant>? ParticipantJoined;
    event Action<SessionParticipant>? ParticipantLeft;
    event Action<SessionJoinResult>? SessionJoined;
    event Action<string>? SessionEnded;
    event Action<Exception?>? ConnectionClosed;

    Task PublishPeerCandidateAsync(PeerCandidate candidate, CancellationToken cancellationToken = default);
    Task PublishPeerKeyOfferAsync(PeerKeyOffer offer, CancellationToken cancellationToken = default);
    Task PublishPeerKeyProofAsync(
        string toConnectionId,
        byte[] transcriptHash,
        byte[] proof,
        CancellationToken cancellationToken = default);
}
