namespace ListenTogether.Core.Audio;

/// <summary>
/// Describes what the host has chosen to share. System audio is exclusive;
/// application mode can contain one or more application processes.
/// </summary>
public sealed class AudioSourceSelection
{
    private AudioSourceSelection(AudioSourceKind kind, IReadOnlyList<AudioSource> sources)
    {
        Kind = kind;
        Sources = sources;
    }

    public AudioSourceKind Kind { get; }
    public IReadOnlyList<AudioSource> Sources { get; }

    public static AudioSourceSelection System(AudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Kind != AudioSourceKind.System)
            throw new ArgumentException("The selected source must represent system audio.", nameof(source));

        return new AudioSourceSelection(AudioSourceKind.System, [source]);
    }

    public static AudioSourceSelection Applications(IEnumerable<AudioSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var selected = sources.ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("Select at least one application to share.", nameof(sources));

        if (selected.Any(source => source is null || source.Kind != AudioSourceKind.Application))
            throw new ArgumentException("Only application audio sources can be selected in application mode.", nameof(sources));

        var duplicateProcessIds = selected
            .GroupBy(source => source.ProcessId)
            .Any(group => group.Count() > 1);

        if (duplicateProcessIds)
            throw new ArgumentException("An application can only be selected once.", nameof(sources));

        return new AudioSourceSelection(AudioSourceKind.Application, Array.AsReadOnly(selected));
    }
}
