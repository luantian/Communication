using System.Buffers.Binary;

namespace IndustrialCommunication.Mitsubishi;

/// <summary>
/// MC-protocol 3E/4E binary frame construction and parsing (pure functions).
///
/// Request:  [3E] 50 00 | route(5) | len(2) | mon(2) cmd(2) sub(2) addr(3) code(1) count(2) data...
///           [4E] 54 00 | serial(2) | route(5) | len(2) | ...
/// Response: [3E] D0 00 | route(5) | len(2) | endCode(2) | data...
///           [4E] D4 00 | serial(2) | route(5) | len(2) | end(2) | data...
///
/// All multi-byte fields little-endian. Bit data: 2 bits per byte — first bit in the high nibble (0x10),
/// second bit in the low nibble (0x01). Word data: 2 bytes per word, little-endian.
/// </summary>
internal static class McFrame
{
    public const ushort CmdBatchRead = 0x0401;
    public const ushort CmdBatchWrite = 0x1401;
    public const ushort SubCommandWord = 0x0000;
    public const ushort SubCommandBit = 0x0001;

    public const int MaxBitsPerRequest = 7168;
    public const int MaxWordsPerRequest = 448;

    public static byte[] BuildRequest(
        McFrameKind frame,
        ushort serial,
        byte networkNo,
        byte pcNo,
        byte stationNo,
        ushort monitorTimerMs,
        ushort command,
        ushort subCommand,
        int deviceNumber,
        byte deviceCode,
        ushort count,
        ReadOnlySpan<byte> writeData)
    {
        // content = monitor timer + command + subcommand + address + code + count + data
        var contentLength = 2 + 2 + 2 + 3 + 1 + 2 + writeData.Length;
        var request = new byte[
            2                                   // subheader
            + (frame == McFrameKind.Frame4E ? 2 : 0) // serial (4E)
            + 5                                 // route: network, pc, io(2), station
            + 2                                 // request data length
            + contentLength];

        var i = 0;
        request[i++] = frame == McFrameKind.Frame4E ? (byte)0x54 : (byte)0x50;
        request[i++] = 0x00;

        if (frame == McFrameKind.Frame4E)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(i), serial);
            i += 2;
        }

        request[i++] = networkNo;
        request[i++] = pcNo;
        request[i++] = 0xFF;               // request target module IO number (low)
        request[i++] = 0x03;               // request target module IO number (high) = 03FF
        request[i++] = stationNo;

        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(i), (ushort)contentLength);
        i += 2;

        // The monitor timer is transmitted in 250 ms units.
        var timerUnits = (ushort)Math.Clamp(monitorTimerMs / 250, 0, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(i), timerUnits);
        i += 2;

        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(i), command);
        i += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(i), subCommand);
        i += 2;

        request[i++] = (byte)(deviceNumber & 0xFF);
        request[i++] = (byte)((deviceNumber >> 8) & 0xFF);
        request[i++] = (byte)((deviceNumber >> 16) & 0xFF);
        request[i++] = deviceCode;

        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(i), count);
        i += 2;

        writeData.CopyTo(request.AsSpan(i));
        return request;
    }

    /// <summary>Returns the payload after the end code; throws <see cref="InvalidDataException"/> on malformed frames.</summary>
    public static ReadOnlyMemory<byte> ParseResponse(ReadOnlyMemory<byte> frame, McFrameKind kind, ushort expectedSerial)
    {
        var span = frame.Span;
        int i = 0;

        ExpectByte(span, ref i, kind == McFrameKind.Frame4E ? (byte)0xD4 : (byte)0xD0, "response subheader");
        ExpectByte(span, ref i, 0x00, "response subheader");

        if (kind == McFrameKind.Frame4E)
        {
            var serial = BinaryPrimitives.ReadUInt16LittleEndian(span[i..]);
            if (serial != expectedSerial)
                throw new InvalidDataException($"Response serial {serial} does not match request serial {expectedSerial}.");
            i += 2;
        }

        i += 5; // route

        if (span.Length < i + 2)
            throw new InvalidDataException("MC response is too short for a length field.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(span[i..]);
        i += 2;

        if (span.Length < i + 2)
            throw new InvalidDataException("MC response is too short for an end code.");
        var endCode = BinaryPrimitives.ReadUInt16LittleEndian(span[i..]);
        i += 2;

        if (endCode != 0)
            throw new McEndCodeException(endCode);

        var data = frame.Slice(i);
        if (data.Length + 2 != length)
            throw new InvalidDataException(
                $"MC response length field says {length} but {data.Length + 2} bytes of end code + data are present.");

        return data;
    }

    /// <summary>Packs bits two per byte: first bit high nibble, second bit low nibble.</summary>
    public static byte[] PackBits(IReadOnlyList<bool> bits)
    {
        var bytes = new byte[(bits.Count + 1) / 2];
        for (int i = 0; i < bits.Count; i++)
        {
            if ((i & 1) == 0)
                bytes[i / 2] |= (byte)(bits[i] ? 0x10 : 0x00);
            else
                bytes[i / 2] |= (byte)(bits[i] ? 0x01 : 0x00);
        }
        return bytes;
    }

    /// <summary>Unpacks the first <paramref name="count"/> bits from two-per-byte nibble packing.</summary>
    public static bool[] UnpackBits(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length < (count + 1) / 2)
            throw new InvalidDataException(
                $"MC bit response carries {data.Length} bytes but {(count + 1) / 2} are required for {count} bits.");

        var bits = new bool[count];
        for (int i = 0; i < count; i++)
            bits[i] = (i & 1) == 0
                ? (data[i / 2] & 0x10) != 0
                : (data[i / 2] & 0x01) != 0;
        return bits;
    }

    public static ushort[] WordsFromLeBytes(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length < count * 2)
            throw new InvalidDataException(
                $"MC word response carries {data.Length} bytes but {count * 2} are required for {count} words.");

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

    private static void ExpectByte(ReadOnlySpan<byte> span, ref int index, byte expected, string what)
    {
        if (span.Length <= index)
            throw new InvalidDataException($"MC response is too short for the {what}.");
        if (span[index] != expected)
            throw new InvalidDataException(
                $"MC response {what} is 0x{span[index]:X2}; expected 0x{expected:X2}.");
        index++;
    }
}

/// <summary>MC end code != 0 — the PLC processed the request and rejected it.</summary>
internal sealed class McEndCodeException : Exception
{
    public ushort EndCode { get; }

    public McEndCodeException(ushort endCode)
        : base($"The PLC answered with MC end code 0x{endCode:X4}.")
    {
        EndCode = endCode;
    }
}
