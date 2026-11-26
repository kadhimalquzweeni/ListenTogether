using ListenTogether.Core.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class AudioSourceSelectionTests
{
    [Fact]
    public void System_AcceptsSystemSource()
    {
        var source = new AudioSource("system:default", "Default output", AudioSourceKind.System);

        var selection = AudioSourceSelection.System(source);

        Assert.Equal(AudioSourceKind.System, selection.Kind);
        Assert.Single(selection.Sources);
        Assert.Same(source, selection.Sources[0]);
    }

    [Fact]
    public void System_RejectsApplicationSource()
    {
        var source = new AudioSource("process:42", "Music Player", AudioSourceKind.Application, 42);

        Assert.Throws<ArgumentException>(() => AudioSourceSelection.System(source));
    }

    [Fact]
    public void Applications_AcceptsMultipleDistinctProcesses()
    {
        var first = new AudioSource("process:42", "Music Player", AudioSourceKind.Application, 42);
        var second = new AudioSource("process:84", "Web Browser", AudioSourceKind.Application, 84);

        var selection = AudioSourceSelection.Applications([first, second]);

        Assert.Equal(AudioSourceKind.Application, selection.Kind);
        Assert.Equal(2, selection.Sources.Count);
    }

    [Fact]
    public void Applications_RejectsEmptySelection()
    {
        Assert.Throws<ArgumentException>(() => AudioSourceSelection.Applications([]));
    }

    [Fact]
    public void Applications_RejectsDuplicateProcessIds()
    {
        var first = new AudioSource("first", "Music Player", AudioSourceKind.Application, 42);
        var duplicate = new AudioSource("second", "Music Player (second window)", AudioSourceKind.Application, 42);

        Assert.Throws<ArgumentException>(() => AudioSourceSelection.Applications([first, duplicate]));
    }

    [Fact]
    public void Applications_RejectsSystemSource()
    {
        var system = new AudioSource("system:default", "Default output", AudioSourceKind.System);

        Assert.Throws<ArgumentException>(() => AudioSourceSelection.Applications([system]));
    }

    [Theory]
    [InlineData("", "Application", 42)]
    [InlineData("app", "", 42)]
    [InlineData("app", "Application", 0)]
    [InlineData("app", "Application", -1)]
    public void AudioSource_RejectsInvalidApplicationDetails(string id, string name, int processId)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new AudioSource(id, name, AudioSourceKind.Application, processId));
    }
}
