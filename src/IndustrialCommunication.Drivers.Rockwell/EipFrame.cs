using System.Buffers.Binary;

namespace IndustrialCommunication.Rockwell;

/// <summary>
/// EtherNet/IP encapsulation + CIP unconnected request building and response parsing (pure functions).
/// All integers little-endian. Layout:
///   Encapsulation: command(2) length(2) session(4) status(4) context(8) options(4)
///   SendRRData body: interface(4)=0 timeout(2) itemCount(2)=2 NAI(type 0,len 0) UDI(type 0xB2,len,data)
///   UDI: UnconnectedSend(0x52) path=20 06 24 01 tick 0A 0E embeddedLen(2) CIP-request [pad] route(words,0,port,slot)
/// </summary>
internal static class EipFrame
{
    public const ushort CmdRegisterSession = 0x0065;
    public const ushort CmdSendRRData = 0x006F;
    public const ushort CmdUnregisterSession = 0x0066;

    public const byte ServiceReadTag = 0x4C;
    public const byte ServiceWriteTag = 0x4D;
    public const byte ServiceReadTagFragmented = 0x52;
    public const byte ServiceWriteTagFragmented = 0x53;

    /// <summary>CIP status 0x06 in a fragmented read: partial data, keep going with a bigger offset.</summary>
    public const byte StatusPartialData = 0x06;
    public const byte ServiceGetAttributesAll = 0x01;

    // CIP atomic type codes
    public const ushort TypeBool = 0xC1;
    public const ushort TypeSint = 0xC2;
    public const ushort TypeInt = 0xC3;
    public const ushort TypeDint = 0xC4;
    public const ushort TypeLint = 0xC5;
    public const ushort TypeReal = 0xCA;
    public const ushort TypeLreal = 0xCB;

    /// <summary>Structure/UDT replies carry a fixed "A0 02" type field plus a 2-byte structure handle;
    /// this constant is the little-endian value of that field (0xA0 is the structure marker).</summary>
    public const ushort TypeStructure = 0x02A0;

    public static byte[] BuildRegisterSession() =>
    [
        0x65, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, // protocol version 1, flags 0
    ];

    public static byte[] BuildUnregisterSession(uint sessionHandle) =>
        BuildEncapsulation(CmdUnregisterSession, sessionHandle, 0, []);

    public static uint ParseRegisterSessionResponse(ReadOnlySpan<byte> frame)
    {
        ValidateEncapsulation(frame, CmdRegisterSession);
        return BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]);
    }

    public static byte[] BuildReadTagRequest(uint sessionHandle, ulong senderContext, byte slot, byte[] ioi, ushort elementCount)
    {
        var cip = new byte[2 + ioi.Length + 2];
        cip[0] = ServiceReadTag;
        cip[1] = checked((byte)(ioi.Length / 2));
        ioi.AsSpan().CopyTo(cip.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(cip.AsSpan(2 + ioi.Length), elementCount);
        return BuildSendRRData(sessionHandle, senderContext, slot, cip);
    }

    public static byte[] BuildReadTagFragmentedRequest(uint sessionHandle, ulong senderContext, byte slot, byte[] ioi, ushort elementCount, uint byteOffset)
    {
        // 0x52 Read Tag Fragmented: same as 0x4C plus a UDINT byte offset after the element count.
        var cip = new byte[2 + ioi.Length + 2 + 4];
        cip[0] = ServiceReadTagFragmented;
        cip[1] = checked((byte)(ioi.Length / 2));
        ioi.AsSpan().CopyTo(cip.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(cip.AsSpan(2 + ioi.Length), elementCount);
        BinaryPrimitives.WriteUInt32LittleEndian(cip.AsSpan(4 + ioi.Length), byteOffset);
        return BuildSendRRData(sessionHandle, senderContext, slot, cip);
    }

    public static byte[] BuildWriteTagFragmentedRequest(uint sessionHandle, ulong senderContext, byte slot, byte[] ioi, ushort typeCode, ushort elementCount, uint byteOffset, ReadOnlySpan<byte> data)
    {
        // 0x53 Write Tag Fragmented: same as 0x4D plus a UDINT byte offset before the data.
        var cip = new byte[2 + ioi.Length + 2 + 2 + 4 + data.Length];
        cip[0] = ServiceWriteTagFragmented;
        cip[1] = checked((byte)(ioi.Length / 2));
        ioi.AsSpan().CopyTo(cip.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(cip.AsSpan(2 + ioi.Length), typeCode);
        BinaryPrimitives.WriteUInt16LittleEndian(cip.AsSpan(4 + ioi.Length), elementCount);
        BinaryPrimitives.WriteUInt32LittleEndian(cip.AsSpan(6 + ioi.Length), byteOffset);
        data.CopyTo(cip.AsSpan(10 + ioi.Length));
        return BuildSendRRData(sessionHandle, senderContext, slot, cip);
    }

    public static byte[] BuildWriteTagRequest(uint sessionHandle, ulong senderContext, byte slot, byte[] ioi, ushort typeCode, ushort elementCount, ReadOnlySpan<byte> data)
    {
        var cip = new byte[2 + ioi.Length + 2 + 2 + data.Length];
        cip[0] = ServiceWriteTag;
        cip[1] = checked((byte)(ioi.Length / 2));
        ioi.AsSpan().CopyTo(cip.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(cip.AsSpan(2 + ioi.Length), typeCode);
        BinaryPrimitives.WriteUInt16LittleEndian(cip.AsSpan(4 + ioi.Length), elementCount);
        data.CopyTo(cip.AsSpan(6 + ioi.Length));
        return BuildSendRRData(sessionHandle, senderContext, slot, cip);
    }

    public static byte[] BuildHeartbeatRequest(uint sessionHandle, ulong senderContext, byte slot) =>
        // Get Attributes All on the Identity object (class 01, instance 01).
        BuildSendRRData(sessionHandle, senderContext, slot, [ServiceGetAttributesAll, 0x02, 0x20, 0x01, 0x24, 0x01]);

    public sealed record CipResponse(byte GeneralStatus, byte[] AdditionalStatus, ushort? DataType, ReadOnlyMemory<byte> Data);

    /// <summary>Parses a SendRRData response; throws <see cref="EipCipException"/> on non-zero CIP status.
    /// With <paramref name="allowPartial"/> a fragmented-read status 0x06 yields the partial data instead.</summary>
    public static CipResponse ParseCipResponse(ReadOnlyMemory<byte> frame, byte expectedService, bool allowPartial = false)
    {
        var span = frame.Span;
        ValidateEncapsulation(span, CmdSendRRData);

        // CPF: interface(4) timeout(2) itemCount(2) NAI type(2)+len(2) UDI type(2)+len(2) → CIP at offset 40
        if (span.Length < 44)
            throw new InvalidDataException("EtherNet/IP response is too short for a CIP reply.");
        var itemTypes = BinaryPrimitives.ReadUInt16LittleEndian(span[30..]);
        if (itemTypes != 2)
            throw new InvalidDataException($"EtherNet/IP response itemCount is {itemTypes}; expected 2.");

        var cip = span[40..];
        var service = cip[0];
        if (service != (byte)(expectedService | 0x80))
            throw new InvalidDataException(
                $"CIP response service is 0x{service:X2}; expected 0x{expectedService | 0x80:X2}.");

        var status = cip[2];
        var additionalWords = cip[3];
        if (status != 0 && !(allowPartial && status == StatusPartialData))
            throw new EipCipException(status, DescribeStatus(status));

        var data = cip[(4 + additionalWords * 2)..];
        if (expectedService is ServiceReadTag or ServiceReadTagFragmented)
        {
            // Atomic types: 2-byte code. Structures/UDTs: "A0 02" + 2-byte structure handle (4 bytes total).
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data);
            var typeFieldLength = type == TypeStructure ? 4 : 2;
            return new CipResponse(status, [], type, frame.Slice(44 + additionalWords * 2 + typeFieldLength));
        }

        return new CipResponse(status, [], null, Memory<byte>.Empty);
    }

    public static ushort[] WordsFromLeBytes(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length < count * 2)
            throw new InvalidDataException(
                $"CIP read returned {data.Length} byte(s) but {count * 2} are required for {count} words.");

        var words = new ushort[count];
        for (int i = 0; i < count; i++)
            words[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(2 * i)..]);
        return words;
    }

    public static byte[] WordsToLeBytes(IReadOnlyList<ushort> words)
    {
        var bytes = new byte[words.Count * 2];
        for (int i = 0; i < words.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), words[i]);
        return bytes;
    }

    private static byte[] BuildSendRRData(uint sessionHandle, ulong senderContext, byte slot, byte[] cip)
    {
        // Unconnected Send wrapper: service 0x52 to the Connection Manager + tick + embedded length +
        // CIP request + optional odd-padding + route (word count, reserved, port 1 backplane, slot).
        var padded = (cip.Length & 1) == 1 ? cip.Length + 1 : cip.Length;
        var udi = new byte[10 + padded + 4];
        udi[0] = 0x52;
        udi[1] = 0x02;
        udi[2] = 0x20;
        udi[3] = 0x06;
        udi[4] = 0x24;
        udi[5] = 0x01;
        udi[6] = 0x0A;
        udi[7] = 0x0E;
        BinaryPrimitives.WriteUInt16LittleEndian(udi.AsSpan(8), (ushort)cip.Length);
        cip.AsSpan().CopyTo(udi.AsSpan(10));
        udi[^4] = 0x01; // route: one word
        udi[^3] = 0x00;
        udi[^2] = 0x01; // port 1 = backplane
        udi[^1] = slot;

        var body = new byte[16 + udi.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0), 0);          // interface handle
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 1);         // timeout
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 2);         // item count
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), 0x0000);    // null address item
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10), 0);        // NAI length
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12), 0x00B2);   // unconnected data item
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), (ushort)udi.Length);
        udi.AsSpan().CopyTo(body.AsSpan(16));

        return BuildEncapsulation(CmdSendRRData, sessionHandle, (ulong)body.Length, body, senderContext);
    }

    private static byte[] BuildEncapsulation(ushort command, uint sessionHandle, ulong senderContext, byte[] body) =>
        BuildEncapsulation(command, sessionHandle, (ulong)body.Length, body, senderContext);

    private static byte[] BuildEncapsulation(ushort command, uint sessionHandle, ulong bodyLength, byte[] body, ulong senderContext = 0)
    {
        var frame = new byte[24 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0), command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)bodyLength);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), sessionHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), 0); // status
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(12), senderContext);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(20), 0); // options
        body.AsSpan().CopyTo(frame.AsSpan(24));
        return frame;
    }

    private static void ValidateEncapsulation(ReadOnlySpan<byte> frame, ushort expectedCommand)
    {
        if (frame.Length < 24)
            throw new InvalidDataException("EtherNet/IP frame is too short for an encapsulation header.");

        var command = BinaryPrimitives.ReadUInt16LittleEndian(frame);
        if (command != expectedCommand)
            throw new InvalidDataException($"Encapsulation command is 0x{command:X4}; expected 0x{expectedCommand:X4}.");

        var length = BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]);
        if (frame.Length != 24 + length)
            throw new InvalidDataException(
                $"Encapsulation length says {length} but {frame.Length - 24} body bytes are present.");

        var status = BinaryPrimitives.ReadUInt32LittleEndian(frame[8..]);
        if (status != 0)
            throw new EipEncapsulationException(status);
    }

    private static string DescribeStatus(byte status) => status switch
    {
        0x04 => "path segment error — the tag does not exist or the IOI cannot be resolved",
        0x05 => "path destination unknown",
        0x06 => "partial data — the reply needs fragmented reads",
        0x08 => "service not supported",
        0x10 => "device state error (e.g. key switch position)",
        0x13 => "not enough data for the write",
        0x15 => "too much data for the write",
        0x16 => "object does not exist",
        0x20 => "invalid parameter",
        0x26 => "IOI path size mismatch",
        _ => "unknown CIP error",
    };
}

internal sealed class EipCipException : Exception
{
    public byte Status { get; }

    public EipCipException(byte status, string description)
        : base($"The controller answered with CIP status 0x{status:X2} ({description}).")
    {
        Status = status;
    }
}

internal sealed class EipEncapsulationException : Exception
{
    public uint Status { get; }

    public EipEncapsulationException(uint status)
        : base($"EtherNet/IP encapsulation status is 0x{status:X8}.")
    {
        Status = status;
    }
}
