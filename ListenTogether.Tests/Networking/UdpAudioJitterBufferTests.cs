using ListenTogether.Infrastructure.Networking;

namespace ListenTogether.Tests.Networking;

public sealed class UdpAudioJitterBufferTests
{
    [Fact]
    public void DrainReady_ReordersFramesWithinStartupWindow()
    {
        var start = DateTimeOffset.UtcNow;
        var buffer = new UdpAudioJitterBuffer(TimeSpan.FromMilliseconds(40));

        Assert.True(buffer.TryEnqueue(2, new byte[] { 2 }, start));
        Assert.True(buffer.TryEnqueue(1, new byte[] { 1 }, start.AddMilliseconds(10)));

        Assert.Empty(buffer.DrainReady(start.AddMilliseconds(39)));
        var frames = buffer.DrainReady(start.AddMilliseconds(50));

        Assert.Equal(new ulong[] { 1, 2 }, frames.Select(frame => frame.Sequence));
        Assert.Equal(new byte[] { 1 }, frames[0].Payload);
        Assert.Equal(new byte[] { 2 }, frames[1].Payload);
    }

    [Fact]
    public void DrainReady_SkipsLostFrameAfterGapDeadline()
    {
        var start = DateTimeOffset.UtcNow;
        var buffer = new UdpAudioJitterBuffer(TimeSpan.FromMilliseconds(40));

        Assert.True(buffer.TryEnqueue(1, new byte[] { 1 }, start));
        Assert.True(buffer.TryEnqueue(3, new byte[] { 3 }, start));
        Assert.Equal(new ulong[] { 1 }, buffer.DrainReady(start.AddMilliseconds(40)).Select(frame => frame.Sequence));

        Assert.Empty(buffer.DrainReady(start.AddMilliseconds(79)));
        Assert.Equal(new ulong[] { 3 }, buffer.DrainReady(start.AddMilliseconds(81)).Select(frame => frame.Sequence));
    }

    [Fact]
    public void TryEnqueue_RejectsDuplicatesAndFramesOlderThanPlaybackCursor()
    {
        var start = DateTimeOffset.UtcNow;
        var buffer = new UdpAudioJitterBuffer(TimeSpan.Zero);

        Assert.True(buffer.TryEnqueue(5, new byte[] { 5 }, start));
        Assert.False(buffer.TryEnqueue(5, new byte[] { 5 }, start));
        Assert.Equal(new ulong[] { 5 }, buffer.DrainReady(start).Select(frame => frame.Sequence));
        Assert.False(buffer.TryEnqueue(5, new byte[] { 5 }, start.AddMilliseconds(1)));
        Assert.False(buffer.TryEnqueue(4, new byte[] { 4 }, start.AddMilliseconds(1)));
    }

    [Fact]
    public void TryEnqueue_EnforcesCapacity()
    {
        var start = DateTimeOffset.UtcNow;
        var buffer = new UdpAudioJitterBuffer(TimeSpan.FromMilliseconds(50), capacity: 2);

        Assert.True(buffer.TryEnqueue(1, new byte[] { 1 }, start));
        Assert.True(buffer.TryEnqueue(2, new byte[] { 2 }, start));
        Assert.False(buffer.TryEnqueue(3, new byte[] { 3 }, start));
        Assert.Equal(2, buffer.BufferedFrameCount);
    }

    [Fact]
    public void Clear_ResetsPlaybackCursorAndPendingFrames()
    {
        var start = DateTimeOffset.UtcNow;
        var buffer = new UdpAudioJitterBuffer(TimeSpan.Zero);
        Assert.True(buffer.TryEnqueue(9, new byte[] { 9 }, start));
        Assert.Single(buffer.DrainReady(start));

        buffer.Clear();

        Assert.Equal(0, buffer.BufferedFrameCount);
        Assert.True(buffer.TryEnqueue(1, new byte[] { 1 }, start.AddSeconds(1)));
        Assert.Equal(new ulong[] { 1 }, buffer.DrainReady(start.AddSeconds(1)).Select(frame => frame.Sequence));
    }
}
