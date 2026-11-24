namespace ListenTogether.Core.Audio;

public enum AudioSourceKind
{
    System,
    Application
}

public sealed record AudioSource
{
    public AudioSource(string id, string displayName, AudioSourceKind kind, int? processId = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("An audio source ID is required.", nameof(id));

        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("An audio source display name is required.", nameof(displayName));

        if (kind == AudioSourceKind.Application && processId is null or <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "Application sources require a positive process ID.");

        if (kind == AudioSourceKind.System && processId is not null)
            throw new ArgumentException("System audio sources do not have a process ID.", nameof(processId));

        Id = id;
        DisplayName = displayName;
        Kind = kind;
        ProcessId = processId;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public AudioSourceKind Kind { get; }
    public int? ProcessId { get; }
}
