namespace ListenTogether.Core.Sessions;

public enum SessionRole
{
    Host,
    Listener
}

public sealed record SessionInvitation(string Code, Uri? JoinUri = null);
