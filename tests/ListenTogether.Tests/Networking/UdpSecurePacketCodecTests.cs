using ListenTogether.Infrastructure.Networking;

namespace ListenTogether.Tests.Networking;

public sealed class UdpSecurePacketCodecTests
{
    private static readonly Guid SessionId = Guid.Parse("2f9b5d32-6f22-4a4d-b6d0-10cce0b9b06a");
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] Prefix = [0x11, 0x22, 0x33, 0x44];

    [Fact]
    public void EncryptAndDecrypt_RoundTripsAudioPayload()
    {
        using var sender = new UdpSecurePacketCodec(Key, Prefix);
        using var receiver = new UdpSecurePacketCodec(Key, Prefix);
        byte[] audio = [0, 1, 2, 3, 127, 128, 254, 255];
        var datagram = sender.Encrypt(SessionId, UdpPacketType.Audio, 1, audio);
        Assert.True(receiver.TryDecrypt(datagram, SessionId, out var packet));
        Assert.NotNull(packet);
        Assert.Equal(UdpPacketType.Audio, packet.Type);
        Assert.Equal((ulong)1, packet.Sequence);
        Assert.Equal(audio, packet.Payload);
    }

    [Fact]
    public void TryDecrypt_RejectsDifferentSession()
    {
        using var sender = new UdpSecurePacketCodec(Key, Prefix);
        using var receiver = new UdpSecurePacketCodec(Key, Prefix);
        var datagram = sender.Encrypt(SessionId, UdpPacketType.Audio, 1, [1, 2, 3]);
        Assert.False(receiver.TryDecrypt(datagram, Guid.NewGuid(), out _));
    }

    [Fact]
    public void TryDecrypt_RejectsTamperedCiphertextAndHeader()
    {
        using var sender = new UdpSecurePacketCodec(Key, Prefix);
        using var receiver = new UdpSecurePacketCodec(Key, Prefix);
        var body = sender.Encrypt(SessionId, UdpPacketType.Audio, 1, [1, 2, 3]);
        body[^1] ^= 0x80;
        Assert.False(receiver.TryDecrypt(body, SessionId, out _));
        var header = sender.Encrypt(SessionId, UdpPacketType.Audio, 2, [1, 2, 3]);
        header[5] = (byte)UdpPacketType.KeepAlive;
        Assert.False(receiver.TryDecrypt(header, SessionId, out _));
    }

    [Fact]
    public void TryDecrypt_RejectsReplay()
    {
        using var sender = new UdpSecurePacketCodec(Key, Prefix);
        using var receiver = new UdpSecurePacketCodec(Key, Prefix);
        var datagram = sender.Encrypt(SessionId, UdpPacketType.Audio, 10, [9]);
        Assert.True(receiver.TryDecrypt(datagram, SessionId, out _));
        Assert.False(receiver.TryDecrypt(datagram, SessionId, out _));
    }

    [Fact]
    public void TryDecrypt_AllowsOutOfOrderPacketsWithinReplayWindow()
    {
        using var sender = new UdpSecurePacketCodec(Key, Prefix);
        using var receiver = new UdpSecurePacketCodec(Key, Prefix);
        var later = sender.Encrypt(SessionId, UdpPacketType.Audio, 12, [12]);
        var earlier = sender.Encrypt(SessionId, UdpPacketType.Audio, 10, [10]);
        Assert.True(receiver.TryDecrypt(later, SessionId, out _));
        Assert.True(receiver.TryDecrypt(earlier, SessionId, out _));
        Assert.False(receiver.TryDecrypt(earlier, SessionId, out _));
    }

    [Fact]
    public void TryDecrypt_RejectsPacketsOutsideReplayWindow()
    {
        using var sender = new UdpSecurePacketCodec(Key, Prefix);
        using var receiver = new UdpSecurePacketCodec(Key, Prefix);
        var old = sender.Encrypt(SessionId, UdpPacketType.Audio, 1, [1]);
        var newest = sender.Encrypt(SessionId, UdpPacketType.Audio, 65, [65]);
        Assert.True(receiver.TryDecrypt(newest, SessionId, out _));
        Assert.False(receiver.TryDecrypt(old, SessionId, out _));
    }

    [Fact]
    public void TryDecrypt_RejectsMalformedAndOversizedDatagrams()
    {
        using var receiver = new UdpSecurePacketCodec(Key, Prefix);
        Assert.False(receiver.TryDecrypt([], SessionId, out _));
        Assert.False(receiver.TryDecrypt(new byte[UdpSecurePacketCodec.MaximumDatagramSize + 1], SessionId, out _));
        Assert.False(receiver.TryDecrypt(new byte[UdpSecurePacketCodec.HeaderSize + UdpSecurePacketCodec.AuthenticationTagSize - 1], SessionId, out _));
    }

    [Fact]
    public void Encrypt_RejectsOversizedPayloadAndSequenceZero()
    {
        using var sender = new UdpSecurePacketCodec(Key, Prefix);
        Assert.Throws<ArgumentOutOfRangeException>(() => sender.Encrypt(SessionId, UdpPacketType.Audio, 1,
            new byte[UdpSecurePacketCodec.MaximumDatagramSize]));
        Assert.Throws<ArgumentOutOfRangeException>(() => sender.Encrypt(SessionId, UdpPacketType.Audio, 0, [1]));
    }

    [Fact]
    public void Encrypt_RejectsRepeatedOrDecreasingSequenceToPreventNonceReuse()
    {
        using var sender = new UdpSecurePacketCodec(Key, Prefix);
        sender.Encrypt(SessionId, UdpPacketType.Audio, 5, [1]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            sender.Encrypt(SessionId, UdpPacketType.Audio, 5, [2]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            sender.Encrypt(SessionId, UdpPacketType.Audio, 4, [3]));
        sender.Encrypt(SessionId, UdpPacketType.Audio, 6, [4]);
    }

    [Fact]
    public void Constructor_RequiresAes256KeyAndFourByteNoncePrefix()
    {
        Assert.Throws<ArgumentException>(() => new UdpSecurePacketCodec(new byte[16], Prefix));
        Assert.Throws<ArgumentException>(() => new UdpSecurePacketCodec(Key, new byte[3]));
    }

    [Fact]
    public void TryDecrypt_RejectsDifferentNoncePrefix()
    {
        using var sender = new UdpSecurePacketCodec(Key, Prefix);
        using var receiver = new UdpSecurePacketCodec(Key, [0xAA, 0xBB, 0xCC, 0xDD]);
        var datagram = sender.Encrypt(SessionId, UdpPacketType.Audio, 1, [1]);
        Assert.False(receiver.TryDecrypt(datagram, SessionId, out _));
    }
}
