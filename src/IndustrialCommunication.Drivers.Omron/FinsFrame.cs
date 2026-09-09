using System.Buffers.Binary;

namespace IndustrialCommunication.Omron;

/// <summary>
/// FINS/TCP frame construction and parsing (pure functions).
///
/// TCP handshake (both directions, 16 bytes):
///   "FINS" | length=0x0000000C | command(4, BE) | error(4, BE) | node address(4, BE)
///   client command 0x00000000 (node address data send), server command 0x00000001 (reply).
///
/// Data frames:
///   "FINS" | length(4, BE, bytes after this field) | FINS frame
///   FINS frame = ICF RSV GCT DNA DA1 DA2 SNA SA1 SA2 SID | command(2, BE) | body
///
/// Memory area read (0x0101) body: area(2, BE) word(2, BE) bit(1) count(2, BE) — words come back
/// 2 bytes each big-endian; bits come back 1 byte each.
/// Memory area write (0x0102) body: area(2) word(2) bit(1) count(2) data.
/// </summary>
internal static class FinsFrame
{
    public const ushort CmdMemoryAreaRead = 0x0101;
    public const ushort CmdMemoryAreaWrite = 0x0102;

    public const int MaxWordsPerRequest = 124;
    public const int MaxBitsPerRequest = 1984;

    private static ReadOnlySpan<byte> Magic => "FINS"u8;

    public static byte[] BuildNodeHandshake(byte clientNode)
    {
        var packet = new byte[20];
        Magic.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), 12);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), 0x00000000); // node address data send
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), 0x00000000); // no error
        packet[19] = clientNode; // node address, low byte of the BE field
        return packet;
    }

    /// <summary>Parses the 24-byte handshake reply. Returns the node assigned to this client and the PLC's own node.</summary>
    public static (byte AssignedLocalNode, byte ServerNode) ParseNodeHandshakeResponse(ReadOnlySpan<byte> packet)
    {
        ValidateHandshake(packet);
        var command = BinaryPrimitives.ReadInt32BigEndian(packet[8..]);
        var error = BinaryPrimitives.ReadInt32BigEndian(packet[12..]);
        if (command != 0x00000001)
            throw new InvalidDataException($"FINS/TCP handshake: unexpected command 0x{command:X8}.");
        if (error != 0)
            throw new InvalidDataException($"FINS/TCP handshake rejected by the PLC (error 0x{error:X8}).");
        return (packet[19], packet[23]);
    }

    public static byte[] WrapFinsFrame(ReadOnlySpan<byte> finsFrame)
    {
        var packet = new byte[8 + finsFrame.Length];
        Magic.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), finsFrame.Length);
        finsFrame.CopyTo(packet.AsSpan(8));
        return packet;
    }

    /// <summary>Extracts the FINS frame from a full TCP packet (magic + length + frame).</summary>
    public static ReadOnlyMemory<byte> UnwrapFinsFrame(ReadOnlyMemory<byte> packet)
    {
        var span = packet.Span;
        if (span.Length < 8)
            throw new InvalidDataException("FINS/TCP data frame packet is too short.");
        if (!span[..4].SequenceEqual(Magic))
            throw new InvalidDataException("FINS/TCP data frame packet does not start with the FINS magic.");

        var length = BinaryPrimitives.ReadInt32BigEndian(span[4..]);
        if (packet.Length != 8 + length)
            throw new InvalidDataException(
                $"FINS/TCP length field says {length} but {packet.Length - 8} bytes of frame are present.");
        return packet[8..];
    }

    public static byte[] BuildFinsCommand(
        byte sourceNet,
        byte sourceNode,
        byte destNet,
        byte destNode,
        byte sid,
        ushort command,
        ushort areaCode,
        int wordAddress,
        byte bitAddress,
        ushort count,
        ReadOnlySpan<byte> writeData)
    {
        var frame = new byte[10 + 2 + 7 + writeData.Length];
        var i = 0;
        frame[i++] = 0x80;      // ICF: request with response
        frame[i++] = 0x00;      // RSV
        frame[i++] = 0x02;      // GCT
        frame[i++] = destNet;   // DNA
        frame[i++] = destNode;  // DA1
        frame[i++] = 0x00;      // DA2: CPU unit
        frame[i++] = sourceNet; // SNA
        frame[i++] = sourceNode;// SA1
        frame[i++] = 0x00;      // SA2
        frame[i++] = sid;       // SID

        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(i), command);
        i += 2;

        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(i), areaCode);
        i += 2;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(i), checked((ushort)wordAddress));
        i += 2;
        frame[i++] = bitAddress;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(i), count);
        i += 2;

        writeData.CopyTo(frame.AsSpan(i));
        return frame;
    }

    /// <summary>Returns the payload after the response code. Throws on malformed frames or non-zero response codes.</summary>
    public static ReadOnlyMemory<byte> ParseFinsResponse(ReadOnlyMemory<byte> finsFrame, byte expectedSid)
    {
        var span = finsFrame.Span;
        if (span.Length < 12)
            throw new InvalidDataException("FINS response is too short.");
        if (span[0] != 0xC0)
            throw new InvalidDataException($"FINS response ICF is 0x{span[0]:X2}; expected 0xC0.");
        if (span[9] != expectedSid)
            throw new InvalidDataException(
                $"FINS response SID {span[9]} does not match request SID {expectedSid}.");

        var responseCode = BinaryPrimitives.ReadUInt16BigEndian(span[10..]);
        if (responseCode != 0)
            throw new FinsResponseCodeException(responseCode);

        return finsFrame[12..];
    }

    public static ushort[] WordsFromBeBytes(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length < count * 2)
            throw new InvalidDataException(
                $"FINS word response carries {data.Length} bytes but {count * 2} are required for {count} words.");

        var words = new ushort[count];
        for (int i = 0; i < count; i++)
            words[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(2 * i)..]);
        return words;
    }

    public static byte[] WordsToBeBytes(IReadOnlyList<ushort> words)
    {
        var bytes = new byte[words.Count * 2];
        for (int i = 0; i < words.Count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2 * i), words[i]);
        return bytes;
    }

    public static byte[] BitsToBytes(IReadOnlyList<bool> bits)
    {
        var bytes = new byte[bits.Count];
        for (int i = 0; i < bits.Count; i++)
            bytes[i] = bits[i] ? (byte)1 : (byte)0;
        return bytes;
    }

    public static bool[] BitsFromBytes(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length < count)
            throw new InvalidDataException(
                $"FINS bit response carries {data.Length} bytes but {count} are required.");

        var bits = new bool[count];
        for (int i = 0; i < count; i++)
            bits[i] = (data[i] & 0x01) != 0;
        return bits;
    }

    private static void ValidateHandshake(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 24)
            throw new InvalidDataException("FINS/TCP handshake packet is too short.");
        if (!packet[..4].SequenceEqual(Magic))
            throw new InvalidDataException("FINS/TCP handshake packet does not start with the FINS magic.");
    }
}

/// <summary>FINS response code != 0 — the PLC processed the request and rejected it.</summary>
internal sealed class FinsResponseCodeException : Exception
{
    public ushort ResponseCode { get; }

    public FinsResponseCodeException(ushort responseCode)
        : base($"The PLC answered with FINS response code 0x{responseCode:X4}.")
    {
        ResponseCode = responseCode;
    }
}
