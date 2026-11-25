using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ListenTogether.Server.Networking;

/// <summary>
/// Handles the STUN Binding method (RFC 8489) used to discover a client's
/// public transport address. This deliberately does not implement ICE
/// authentication or act as a general-purpose TURN relay.
/// </summary>
public static class StunMessageProcessor
{
    public const int HeaderLength = 20;
    public const uint MagicCookie = 0x2112A442;
    public const ushort BindingRequest = 0x0001;
    public const ushort BindingSuccessResponse = 0x0101;
    private const ushort XorMappedAddressAttribute = 0x0020;
    private const int MaximumRequestLength = 512;

    public static bool TryCreateBindingResponse(
        ReadOnlySpan<byte> request,
        IPEndPoint remoteEndPoint,
        out byte[] response)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        response = [];

        if (request.Length < HeaderLength || request.Length > MaximumRequestLength)
            return false;

        var messageType = BinaryPrimitives.ReadUInt16BigEndian(request[..2]);
        var messageLength = BinaryPrimitives.ReadUInt16BigEndian(request.Slice(2, 2));
        var cookie = BinaryPrimitives.ReadUInt32BigEndian(request.Slice(4, 4));

        if ((messageType & 0xC000) != 0 ||
            messageType != BindingRequest ||
            cookie != MagicCookie ||
            messageLength % 4 != 0 ||
            request.Length != HeaderLength + messageLength)
        {
            return false;
        }

        if (!HasValidAttributes(request[HeaderLength..]))
            return false;

        var address = remoteEndPoint.Address;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var addressBytes = address.GetAddressBytes();
        var isIpv4 = address.AddressFamily == AddressFamily.InterNetwork;
        var isIpv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        if (!isIpv4 && !isIpv6)
            return false;

        var attributeValueLength = isIpv4 ? 8 : 20;
        response = new byte[HeaderLength + 4 + attributeValueLength];
        var output = response.AsSpan();

        BinaryPrimitives.WriteUInt16BigEndian(output[..2], BindingSuccessResponse);
        BinaryPrimitives.WriteUInt16BigEndian(output.Slice(2, 2), (ushort)(4 + attributeValueLength));
        BinaryPrimitives.WriteUInt32BigEndian(output.Slice(4, 4), MagicCookie);
        request.Slice(8, 12).CopyTo(output.Slice(8, 12));

        var attribute = output.Slice(HeaderLength, 4 + attributeValueLength);
        BinaryPrimitives.WriteUInt16BigEndian(attribute[..2], XorMappedAddressAttribute);
        BinaryPrimitives.WriteUInt16BigEndian(attribute.Slice(2, 2), (ushort)attributeValueLength);
        attribute[4] = 0;
        attribute[5] = isIpv4 ? (byte)0x01 : (byte)0x02;

        BinaryPrimitives.WriteUInt16BigEndian(
            attribute.Slice(6, 2),
            (ushort)(remoteEndPoint.Port ^ (MagicCookie >> 16)));

        if (isIpv4)
        {
            var cookieBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(cookieBytes, MagicCookie);
            for (var index = 0; index < 4; index++)
                attribute[8 + index] = (byte)(addressBytes[index] ^ cookieBytes[index]);
        }
        else
        {
            Span<byte> mask = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(mask[..4], MagicCookie);
            request.Slice(8, 12).CopyTo(mask[4..]);
            for (var index = 0; index < 16; index++)
                attribute[8 + index] = (byte)(addressBytes[index] ^ mask[index]);
        }

        return true;
    }

    private static bool HasValidAttributes(ReadOnlySpan<byte> attributes)
    {
        var offset = 0;
        while (offset < attributes.Length)
        {
            if (attributes.Length - offset < 4)
                return false;

            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(attributes.Slice(offset + 2, 2));
            var paddedLength = (valueLength + 3) & ~3;
            if (paddedLength > attributes.Length - offset - 4)
                return false;

            offset += 4 + paddedLength;
        }

        return offset == attributes.Length;
    }
}
