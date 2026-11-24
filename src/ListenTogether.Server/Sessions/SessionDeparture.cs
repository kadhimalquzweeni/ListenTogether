namespace ListenTogether.Server.Sessions;

public sealed record SessionDeparture(
    string Code,
    ListenTogether.Core.Sessions.SessionParticipant Participant,
    bool IsHost);
