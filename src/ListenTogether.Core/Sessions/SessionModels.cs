namespace ListenTogether.Core.Sessions;

public enum SessionRole
{
    Host,
    Listener
}

public sealed record SessionInvitation(string Code, Uri? JoinUri = null);

public sealed record SessionParticipant(string DisplayName, bool IsHost);

public sealed record SessionJoinResult(
    SessionInvitation Invitation,
    string HostDisplayName,
    IReadOnlyList<SessionParticipant> Participants,
    SessionParticipant JoinedParticipant);
