namespace ListenTogether.Core.Audio;

/// <summary>A Windows audio render endpoint that can be selected for capture or playback.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (Default)" : Name;
}
