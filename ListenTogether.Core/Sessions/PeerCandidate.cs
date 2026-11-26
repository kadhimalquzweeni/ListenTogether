using System.Net;

namespace ListenTogether.Core.Sessions;

/// <summary>
/// A UDP endpoint a participant can use when attempting a direct peer connection.
/// Addresses are IP literals rather than host names so candidates cannot trigger DNS lookups.
/// </summary>
public sealed record PeerCandidate(string Address, int Port, PeerCandidateType Type)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Address) &&
        IPAddress.TryParse(Address, out var address) &&
        address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork
            or System.Net.Sockets.AddressFamily.InterNetworkV6 &&
        Port is > 0 and <= 65535 &&
        Enum.IsDefined(Type);
}

public enum PeerCandidateType
{
    Host,
    ServerReflexive,
    Relay
}

/// <summary>
/// A candidate received from another participant in the same listening session.
/// The connection ID is scoped to the current signalling connection.
/// </summary>
public sealed record PeerCandidateMessage(string PeerConnectionId, PeerCandidate Candidate);
