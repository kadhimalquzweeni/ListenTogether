using System.Buffers.Binary;
using System.Net;
using ListenTogether.Server.Networking;

namespace ListenTogether.Tests.Networking;

public sealed class StunMessageProcessorTests
{
    [Fact]
    public void TryCreateBindingResponse_ReturnsXorMappedIpv4Address()
    {
        var transactionId = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();
        var request = CreateBindingRequest(transactionId);
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 54321);

        var created = StunMessageProcessor.TryCreateBindingResponse(request, endpoint, out var response);

        Assert.True(created);
        Assert.Equal(32, response.Length);
        Assert.Equal(StunMessageProcessor.BindingSuccessResponse,
            BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0, 2)));
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2)));
        Assert.Equal(transactionId, response.AsSpan(8, 12).ToArray());
        Assert.Equal((ushort)0x0020, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(20, 2)));
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(22, 2)));
        Assert.Equal((byte)0x01, response[25]);

        var expectedPort = (ushort)(54321 ^ (StunMessageProcessor.MagicCookie >> 16));
        Assert.Equal(expectedPort, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(26, 2)));

        var expectedAddress = new byte[] { 203, 0, 113, 10 };
        var cookie = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(cookie, StunMessageProcessor.MagicCookie);
        for (var index = 0; index < 4; index++)
            expectedAddress[index] ^= cookie[index];
        Assert.Equal(expectedAddress, response.AsSpan(28, 4).ToArray());
    }

    [Fact]
    public void TryCreateBindingResponse_ReturnsXorMappedIpv6Address()
    {
        var transactionId = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();
        var request = CreateBindingRequest(transactionId);
        var endpoint = new IPEndPoint(IPAddress.Parse("2001:db8::1234"), 40000);

        var created = StunMessageProcessor.TryCreateBindingResponse(request, endpoint, out var response);

        Assert.True(created);
        Assert.Equal(44, response.Length);
        Assert.Equal((ushort)20, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(22, 2)));
        Assert.Equal((byte)0x02, response[25]);

        var mask = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mask.AsSpan(0, 4), StunMessageProcessor.MagicCookie);
        transactionId.CopyTo(mask, 4);
        var expectedAddress = endpoint.Address.GetAddressBytes();
        for (var index = 0; index < 16; index++)
            expectedAddress[index] ^= mask[index];

        Assert.Equal(expectedAddress, response.AsSpan(28, 16).ToArray());
    }

    [Fact]
    public void TryCreateBindingResponse_RejectsWrongMessageType()
    {
        var request = CreateBindingRequest();
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), 0x0002);

        Assert.False(StunMessageProcessor.TryCreateBindingResponse(
            request, new IPEndPoint(IPAddress.Loopback, 1234), out var response));
        Assert.Empty(response);
    }

    [Fact]
    public void TryCreateBindingResponse_RejectsInvalidMagicCookie()
    {
        var request = CreateBindingRequest();
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), 0);

        Assert.False(StunMessageProcessor.TryCreateBindingResponse(
            request, new IPEndPoint(IPAddress.Loopback, 1234), out _));
    }

    [Fact]
    public void TryCreateBindingResponse_RejectsIncorrectMessageLength()
    {
        var request = CreateBindingRequest();
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 4);

        Assert.False(StunMessageProcessor.TryCreateBindingResponse(
            request, new IPEndPoint(IPAddress.Loopback, 1234), out _));
    }

    [Fact]
    public void TryCreateBindingResponse_RejectsMalformedAttribute()
    {
        var request = new byte[24];
        WriteHeader(request, 4);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(20, 2), 0x8022);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(22, 2), 8);

        Assert.False(StunMessageProcessor.TryCreateBindingResponse(
            request, new IPEndPoint(IPAddress.Loopback, 1234), out _));
    }

    [Fact]
    public void TryCreateBindingResponse_RejectsOversizedDatagram()
    {
        var request = new byte[StunMessageProcessor.HeaderLength + 512];
        WriteHeader(request, 512);

        Assert.False(StunMessageProcessor.TryCreateBindingResponse(
            request, new IPEndPoint(IPAddress.Loopback, 1234), out _));
    }

    [Fact]
    public void TryCreateBindingResponse_HandlesIpv4MappedIpv6Endpoint()
    {
        var request = CreateBindingRequest();
        var endpoint = new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.25"), 3478);

        var created = StunMessageProcessor.TryCreateBindingResponse(request, endpoint, out var response);

        Assert.True(created);
        Assert.Equal(32, response.Length);
        Assert.Equal((byte)0x01, response[25]);
    }

    private static byte[] CreateBindingRequest(byte[]? transactionId = null)
    {
        var request = new byte[StunMessageProcessor.HeaderLength];
        WriteHeader(request, 0);
        (transactionId ?? new byte[12]).CopyTo(request, 8);
        return request;
    }

    private static void WriteHeader(byte[] request, ushort messageLength)
    {
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), StunMessageProcessor.BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), messageLength);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), StunMessageProcessor.MagicCookie);
    }
}
