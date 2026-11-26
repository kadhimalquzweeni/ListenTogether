using ListenTogether.Core.Audio;
using ListenTogether.Infrastructure.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class AudioCaptureMixerDynamicSourceTests
{
    [Fact]
    public async Task AddSourceAsync_AddsNewApplicationWithoutRestartingExistingCapture()
    {
        var first = new AudioSource("app:1", "Music", AudioSourceKind.Application, 1);
        var second = new AudioSource("app:2", "Browser", AudioSourceKind.Application, 2);
        var factory = new FakeCaptureFactory();
        await using var session = new AudioCaptureMixerSession(AudioSourceSelection.Applications([first]), factory);

        await session.StartAsync();
        var firstCapture = Assert.Single(factory.Captures);
        Assert.True(firstCapture.IsCapturing);

        await session.AddSourceAsync(second);

        Assert.Equal(2, factory.Captures.Count);
        Assert.True(firstCapture.IsCapturing);
        Assert.True(factory.Captures[1].IsCapturing);
    }

    [Fact]
    public async Task AddSourceAsync_RejectsDuplicateSourceAndKeepsExistingCaptureRunning()
    {
        var source = new AudioSource("app:1", "Music", AudioSourceKind.Application, 1);
        var factory = new FakeCaptureFactory();
        await using var session = new AudioCaptureMixerSession(AudioSourceSelection.Applications([source]), factory);

        await session.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AddSourceAsync(source));

        Assert.Single(factory.Captures);
        Assert.True(factory.Captures[0].IsCapturing);
    }

    [Fact]
    public async Task RemoveSourceAsync_StopsOnlyTheRequestedCapture()
    {
        var first = new AudioSource("app:1", "Music", AudioSourceKind.Application, 1);
        var second = new AudioSource("app:2", "Browser", AudioSourceKind.Application, 2);
        var factory = new FakeCaptureFactory();
        await using var session = new AudioCaptureMixerSession(AudioSourceSelection.Applications([first, second]), factory);

        await session.StartAsync();
        var removed = await session.RemoveSourceAsync(first.Id);

        Assert.True(removed);
        Assert.True(factory.Captures[0].IsDisposed);
        Assert.False(factory.Captures[0].IsCapturing);
        Assert.True(factory.Captures[1].IsCapturing);
        Assert.False(await session.RemoveSourceAsync("missing"));
    }

    [Fact]
    public async Task AddAndRemoveSource_ChangesMixedAudioWithoutRestartingTheSession()
    {
        var first = new AudioSource("app:1", "Music", AudioSourceKind.Application, 1);
        var second = new AudioSource("app:2", "Browser", AudioSourceKind.Application, 2);
        var factory = new FakeCaptureFactory();
        await using var session = new AudioCaptureMixerSession(AudioSourceSelection.Applications([first]), factory);
        var received = new TaskCompletionSource<AudioFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.MixedFrameAvailable += frame =>
        {
            if (frame.Samples.Span[0] > 0.29f)
                received.TrySetResult(frame);
        };

        await session.StartAsync();
        await session.AddSourceAsync(second);
        factory.Captures[0].Publish(Enumerable.Repeat(0.1f, 960).ToArray());
        factory.Captures[1].Publish(Enumerable.Repeat(0.2f, 960).ToArray());

        var mixed = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(mixed.Samples.ToArray(), sample => Assert.Equal(0.3f, sample, 3));

        Assert.True(await session.RemoveSourceAsync(second.Id));
        Assert.True(session.IsRunning);
        factory.Captures[0].Publish(Enumerable.Repeat(0.1f, 960).ToArray());
    }

    private sealed class FakeCaptureFactory : IAudioCaptureFactory
    {
        public List<FakeCapture> Captures { get; } = [];

        public Task<IAudioCapture> CreateAsync(AudioSource source, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capture = new FakeCapture(source);
            Captures.Add(capture);
            return Task.FromResult<IAudioCapture>(capture);
        }
    }

    private sealed class FakeCapture(AudioSource source) : IAudioCapture
    {
        public AudioSource Source { get; } = source;
        public AudioFormat Format => AudioFormat.Broadcast;
        public bool IsCapturing { get; private set; }
        public bool IsDisposed { get; private set; }
        public event Action<AudioFrame>? AudioFrameAvailable;
        public event Action<Exception>? CaptureFaulted;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            IsCapturing = true;
        }

        public void Publish(float[] samples)
        {
            if (!IsCapturing)
                throw new InvalidOperationException("Capture is not running.");
            AudioFrameAvailable?.Invoke(new AudioFrame(samples, AudioFormat.Broadcast, DateTimeOffset.UtcNow));
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsCapturing = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            IsCapturing = false;
            return ValueTask.CompletedTask;
        }
    }
}
