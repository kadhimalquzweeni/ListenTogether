using ListenTogether.Core.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class AudioSourceDeviceTests
{
    [Fact]
    public void SystemSource_CanTargetASpecificOutputDevice()
    {
        var source = new AudioSource(
            "system:default-output",
            "All system audio",
            AudioSourceKind.System,
            deviceId: "endpoint-123");

        Assert.Equal("endpoint-123", source.DeviceId);
        Assert.Null(source.ProcessId);
    }

    [Fact]
    public void ApplicationSource_DoesNotAcceptAnOutputDeviceId()
    {
        Assert.Throws<ArgumentException>(() =>
            new AudioSource("process:42", "Music", AudioSourceKind.Application, 42, "endpoint-123"));
    }

    [Fact]
    public void SystemSource_RejectsAnEmptyDeviceId()
    {
        Assert.Throws<ArgumentException>(() =>
            new AudioSource("system", "System", AudioSourceKind.System, deviceId: " "));
    }
}
