using ListenTogether.Core.Audio;
using ListenTogether.Infrastructure.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class AudioCaptureMixerSessionTests
{
    private static readonly AudioFormat Format = AudioFormat.Broadcast;

    [Fact]
    public async Task StartAsync_StartsAllSelectedCapturesAndPublishesMixedBlocks()
    {
        var system = new AudioSource("system", "System", AudioSourceKind.System);
        var app = new AudioSource("app:42", "Music", AudioSourceKind.Application, 42);
        var factory = new FakeCaptureFactory();
        await using var session = new AudioCaptureMixerSession(
            AudioSourceSelection.Applications([app, new AudioSource("app:43", "Browser", AudioSourceKind.Application, 43)]),
            factory,
            TimeSpan.FromMilliseconds(10));
        var received = new TaskCompletionSource<AudioFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.MixedFrameAvailable += frame => received.TrySetResult(frame);

        await session.StartAsync();

        Assert.True(session.IsRunning);
        Assert.Equal(2, factory.Captures.Count);
        Assert.All(factory.Captures, capture => Assert.True(capture.IsCapturing));

        factory.Captures[0].Publish(Enumerable.Repeat(0.2f, 960).ToArray());
        factory.Captures[1].Publish(Enumerable.Repeat(0.3f, 960).ToArray());

        var output = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(480, output.FrameCount);
        Assert.All(output.Samples.ToArray(), sample => Assert.Equal(0.5f, sample, 3));

        await session.StopAsync();
        Assert.False(session.IsRunning);
        Assert.All(factory.Captures, capture => Assert.True(capture.IsDisposed));
    }

    [Fact]
    public async Task StartAsync_WhenFactoryFails_DisposesPreviouslyCreatedCaptures()
    {
        var first = new AudioSource("app:42", "Music", AudioSourceKind.Application, 42);
        var second = new AudioSource("app:43", "Browser", AudioSourceKind.Application, 43);
        var factory = new FakeCaptureFactory(failOnCall: 2);
        await using var session = new AudioCaptureMixerSession(
            AudioSourceSelection.Applications([first, second]), factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());

        Assert.Single(factory.Captures);
        Assert.True(factory.Captures[0].IsDisposed);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task SetGain_ChangesSourceLevelInMixedOutput()
    {
        var source = new AudioSource("app:42", "Music", AudioSourceKind.Application, 42);
        var factory = new FakeCaptureFactory();
        await using var session = new AudioCaptureMixerSession(
            AudioSourceSelection.Applications([source]), factory, TimeSpan.FromMilliseconds(10));
        var received = new TaskCompletionSource<AudioFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.MixedFrameAvailable += frame => received.TrySetResult(frame);

        await session.StartAsync();
        session.SetGain(source.Id, 0.25f);
        factory.Captures[0].Publish(Enumerable.Repeat(0.8f, 960).ToArray());

        var output = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(output.Samples.ToArray(), sample => Assert.Equal(0.2f, sample, 3));
    }

    private sealed class FakeCaptureFactory(int? failOnCall = null) : IAudioCaptureFactory
    {
        public List<FakeCapture> Captures { get; } = [];
        private int _calls;

        public Task<IAudioCapture> CreateAsync(AudioSource source, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls++;
            if (_calls == failOnCall)
                throw new InvalidOperationException("Simulated capture startup failure.");

            var capture = new FakeCapture(source);
            Captures.Add(capture);
            return Task.FromResult<IAudioCapture>(capture);
        }
    }

    private sealed class FakeCapture(AudioSource source) : IAudioCapture
    {
        public AudioSource Source { get; } = source;
        public AudioFormat Format => Format;
        public bool IsCapturing { get; private set; }
        public bool IsDisposed { get; private set; }

        public event Action<AudioFrame>? AudioFrameAvailable;
        public event Action<Exception>? CaptureFaulted;

        public void Start()
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(FakeCapture));
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
