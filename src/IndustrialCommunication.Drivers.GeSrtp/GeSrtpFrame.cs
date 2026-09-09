using System.Buffers.Binary;

namespace IndustrialCommunication.GeSrtp;

/// <summary>
/// GE SRTP frame building and parsing (pure functions). Reverse-engineered protocol (Denton et al. 2017,
/// Wireshark/Spicy dissectors, uGESRTP/jGESRTP): fixed 56-byte header, all integers little-endian,
/// short messages (type 0xC0) carry up to 3 words inline, extended messages (0x80) carry a data buffer
/// after the header with its length in bytes 4–5.
///
/// Read = service 0x04, write = 0x07 (system memory). The memory offset is address−1.
/// Responses: 0xD4 (inline data), 0x94 (data follows as N×2 bytes), 0xD1 (error NACK);
/// primary status at byte 42, secondary at byte 43.
/// </summary>
internal static class GeSrtpFrame
{
    public const byte ServiceReadSystemMemory = 0x04;
    public const byte ServiceWriteSystemMemory = 0x07;

    public const int ShortInlineDataBytes = 6; // bytes 48..53 → 3 words
    public const int MaxWordsPerRequest = 64;

    /// <summary>The connection INIT frame: 56 zero bytes. The PLC answers 0x01 in byte 0.</summary>
    public static byte[] BuildInitFrame() => new byte[56];

    public static byte ParseInitResponse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 56)
            throw new InvalidDataException("SRTP INIT response is shorter than 56 bytes.");
        if (frame[0] != 0x01)
            throw new InvalidDataException($"SRTP INIT was rejected (byte0 = 0x{frame[0]:X2}).");
        return frame[0];
    }

    public static byte[] BuildShortRead(byte sequence, byte selector, int address, ushort units) =>
        BuildShort(ServiceReadSystemMemory, selector, address, units, ReadOnlySpan<byte>.Empty, sequence);

    public static byte[] BuildShortWrite(byte sequence, byte selector, int address, ushort units, ReadOnlySpan<byte> data) =>
        BuildShort(ServiceWriteSystemMemory, selector, address, units, data, sequence);

    private static byte[] BuildShort(byte service, byte selector, int address, ushort units, ReadOnlySpan<byte> data, byte sequence)
    {
        if (data.Length > ShortInlineDataBytes)
            throw new ArgumentException("Short frames carry at most 6 data bytes.");

        var frame = new byte[56];
        frame[0] = 0x02;          // request
        frame[2] = sequence;
        frame[9] = 0x01;
        frame[17] = 0x01;
        frame[30] = sequence;
        frame[31] = 0xC0;         // short message
        frame[36] = 0x10;         // mailbox dest = 3600
        frame[37] = 0x0E;
        frame[40] = 0x01;         // packet number
        frame[41] = 0x01;         // total packets
        frame[42] = service;
        frame[43] = selector;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(44), checked((ushort)(address - 1)));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(46), units);
        data.CopyTo(frame.AsSpan(48));
        return frame;
    }

    public static byte[] BuildExtendedWrite(byte sequence, byte selector, int address, ushort units, ReadOnlySpan<byte> data)
    {
        var frame = new byte[56 + data.Length];
        frame[0] = 0x02;
        frame[2] = sequence;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)data.Length);
        frame[9] = 0x02;
        frame[17] = 0x02;
        frame[30] = sequence;
        frame[31] = 0x80;         // extended message
        frame[36] = 0x10;
        frame[37] = 0x0E;
        frame[40] = 0x01;
        frame[41] = 0x01;
        frame[48] = 0x01;         // packet number (repeat)
        frame[49] = 0x01;         // total packets (repeat)
        frame[50] = ServiceWriteSystemMemory;
        frame[51] = selector;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(52), checked((ushort)(address - 1)));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(54), units);
        data.CopyTo(frame.AsSpan(56));
        return frame;
    }

    public enum ResponseKind
    {
        Inline,      // 0xD4: data in bytes 44..49
        WithBuffer,  // 0x94: N×2 data bytes follow the 56-byte header as a separate TCP frame
        Error,
    }

    public sealed record Response(ResponseKind Kind, byte PrimaryStatus, byte SecondaryStatus, ReadOnlyMemory<byte> Data, ushort BufferedBytes);

    public static Response ParseResponse(ReadOnlyMemory<byte> frame, byte expectedSequence)
    {
        var span = frame.Span;
        if (span.Length < 56)
            throw new InvalidDataException("SRTP response is shorter than 56 bytes.");
        if (span[0] != 0x03)
            throw new InvalidDataException($"SRTP response type is 0x{span[0]:X2}; expected 0x03.");
        if (span[2] != expectedSequence)
            throw new InvalidDataException(
                $"SRTP response sequence {span[2]} does not match request sequence {expectedSequence}.");

        var kind = span[31] switch
        {
            0xD4 => ResponseKind.Inline,
            0x94 => ResponseKind.WithBuffer,
            0xD1 => ResponseKind.Error,
            var other => throw new InvalidDataException($"SRTP response message type 0x{other:X2} is unknown."),
        };

        var primary = span[42];
        var secondary = span[43];
        if (primary != 0)
            throw new GeSrtpStatusException(primary, secondary);

        return kind switch
        {
            ResponseKind.Inline => new Response(kind, primary, secondary, frame.Slice(44, 6), 0),
            ResponseKind.WithBuffer => new Response(kind, primary, secondary, Memory<byte>.Empty,
                BinaryPrimitives.ReadUInt16LittleEndian(span[4..])),
            _ => new Response(kind, primary, secondary, Memory<byte>.Empty, 0),
        };
    }

    public static ushort[] WordsFromLeBytes(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length < count * 2)
            throw new InvalidDataException(
                $"SRTP response carries {data.Length} byte(s) but {count * 2} are required for {count} words.");

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
}

/// <summary>SRTP primary status != 0 — the PLC rejected the request.</summary>
internal sealed class GeSrtpStatusException : Exception
{
    public byte Primary { get; }
    public byte Secondary { get; }

    public GeSrtpStatusException(byte primary, byte secondary)
        : base($"The PLC answered with SRTP status 0x{primary:X2}/0x{secondary:X2} ({Describe(primary)}).")
    {
        Primary = primary;
        Secondary = secondary;
    }

    private static string Describe(byte primary) => primary switch
    {
        0x01 => "illegal service request",
        0x02 => "insufficient privilege level",
        0x04 => "protocol sequence error",
        0x05 => "service request error",
        0x06 => "illegal mailbox type",
        0x07 => "request queue full",
        _ => "unknown error",
    };
}
