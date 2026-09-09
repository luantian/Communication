using System.Buffers.Binary;
using System.Text;

namespace IndustrialCommunication.LsFEnet;

/// <summary>
/// LS Electric XGT dedicated protocol (FEnet TCP 2004) frame building and parsing (pure functions).
///
/// Frame = 20-byte header + instruction area:
///   "LSIS-XGT\0\0" (10) | PLC info (2) | CPU info (1) | frame source (0x33 req / 0x11 resp) |
///   invoke ID (2, LE, echoed) | instruction length (2, LE) | FEnet position (1) | BCC (1)
/// BCC = arithmetic sum of header bytes 0..18 mod 256.
///
/// Instructions (LE): read 0x5400 / read reply 0x5500 / write 0x5800 / write reply 0x5900.
/// Data types: individual BIT 0x0000 / BYTE 0x0100 / WORD 0x0200 / DWORD 0x0300 / LWORD 0x0400,
/// continuous 0x1400 (WORD-sized, no BIT). Read replies carry data from frame offset 32;
/// error status sits at frame offsets 26..27.
/// Variables are ASCII names like "%MW0" (device letter + size letter + decimal address).
/// </summary>
internal static class LsFEnetFrame
{
    // Command/type values are the logical numbers (manual style "h'0054"); the wire bytes are
    // little-endian, so 0x0054 travels as "54 00".
    public const ushort CmdReadRequest = 0x0054;
    public const ushort CmdReadResponse = 0x0055;
    public const ushort CmdWriteRequest = 0x0058;
    public const ushort CmdWriteResponse = 0x0059;
    public const ushort CmdStatusRequest = 0x00B0;
    public const ushort CmdStatusResponse = 0x00B1;

    public const ushort TypeIndividualBit = 0x0000;
    public const ushort TypeIndividualWord = 0x0002;
    public const ushort TypeContinuous = 0x0014;

    /// <summary>Continuous reads are limited to 1400 bytes per request.</summary>
    public const int MaxBytesPerRequest = 1400;

    public const int MaxVariablesPerRequest = 16;

    public static byte[] BuildIndividualRead(ushort invokeId, byte cpuInfo, IReadOnlyList<string> variables)
    {
        var instruction = new MemoryStream();
        WriteInstructionHeader(instruction, CmdReadRequest, TypeIndividualWord);
        WriteU16(instruction, (ushort)variables.Count);
        foreach (var variable in variables)
            WriteVariable(instruction, variable);
        return Wrap(instruction.ToArray(), invokeId, cpuInfo);
    }

    public static byte[] BuildIndividualReadBits(ushort invokeId, byte cpuInfo, IReadOnlyList<string> variables)
    {
        var instruction = new MemoryStream();
        WriteInstructionHeader(instruction, CmdReadRequest, TypeIndividualBit);
        WriteU16(instruction, (ushort)variables.Count);
        foreach (var variable in variables)
            WriteVariable(instruction, variable);
        return Wrap(instruction.ToArray(), invokeId, cpuInfo);
    }

    public static byte[] BuildContinuousRead(ushort invokeId, byte cpuInfo, string variable, ushort byteCount)
    {
        var instruction = new MemoryStream();
        WriteInstructionHeader(instruction, CmdReadRequest, TypeContinuous);
        WriteU16(instruction, 1);
        WriteVariable(instruction, variable);
        WriteU16(instruction, byteCount);
        return Wrap(instruction.ToArray(), invokeId, cpuInfo);
    }

    public static byte[] BuildIndividualWrite(ushort invokeId, byte cpuInfo, IReadOnlyList<(string Variable, byte[] Data)> items)
    {
        var instruction = new MemoryStream();
        WriteInstructionHeader(instruction, CmdWriteRequest, TypeIndividualWord);
        WriteU16(instruction, (ushort)items.Count);
        foreach (var (variable, _) in items)
            WriteVariable(instruction, variable);
        foreach (var (_, data) in items)
        {
            WriteU16(instruction, (ushort)data.Length);
            instruction.Write(data);
        }
        return Wrap(instruction.ToArray(), invokeId, cpuInfo);
    }

    public static byte[] BuildIndividualWriteBits(ushort invokeId, byte cpuInfo, IReadOnlyList<(string Variable, bool On)> items)
    {
        var instruction = new MemoryStream();
        WriteInstructionHeader(instruction, CmdWriteRequest, TypeIndividualBit);
        WriteU16(instruction, (ushort)items.Count);
        foreach (var (variable, _) in items)
            WriteVariable(instruction, variable);
        foreach (var (_, on) in items)
        {
            WriteU16(instruction, 1);
            instruction.WriteByte(on ? (byte)1 : (byte)0);
        }
        return Wrap(instruction.ToArray(), invokeId, cpuInfo);
    }

    public static byte[] BuildStatusRequest(ushort invokeId, byte cpuInfo)
    {
        var instruction = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(instruction.AsSpan(0), CmdStatusRequest);
        return Wrap(instruction, invokeId, cpuInfo);
    }

    public sealed record Response(ushort Command, ushort ErrorStatus, ReadOnlyMemory<byte> Data);

    /// <summary>Parses one complete frame (header + instruction); throws on malformed frames or non-zero error status.</summary>
    public static Response ParseResponse(ReadOnlyMemory<byte> frame, ushort expectedInvokeId)
    {
        var span = frame.Span;
        if (span.Length < 28)
            throw new InvalidDataException("FEnet response is too short.");
        if (!span[..8].SequenceEqual("LSIS-XGT"u8))
            throw new InvalidDataException("FEnet response does not carry the LSIS-XGT company ID.");
        if (span[13] != 0x11)
            throw new InvalidDataException($"FEnet response frame source is 0x{span[13]:X2}; expected 0x11.");

        var invokeId = BinaryPrimitives.ReadUInt16LittleEndian(span[14..]);
        if (invokeId != expectedInvokeId)
            throw new InvalidDataException(
                $"FEnet response invoke ID {invokeId} does not match request {expectedInvokeId}.");

        var length = BinaryPrimitives.ReadUInt16LittleEndian(span[16..]);
        if (span.Length != 20 + length)
            throw new InvalidDataException(
                $"FEnet length says {length} but {span.Length - 20} instruction bytes are present.");

        var command = BinaryPrimitives.ReadUInt16LittleEndian(span[20..]);
        if (command is not (CmdReadResponse or CmdWriteResponse or CmdStatusResponse))
            throw new InvalidDataException($"FEnet response command 0x{command:X4} is unknown.");

        var error = BinaryPrimitives.ReadUInt16LittleEndian(span[26..]);
        if (error != 0)
            throw new LsFEnetStatusException(error);

        // Read replies: 12-byte instruction header (cmd, type, reserved, error) then count (2),
        // data byte count (2), data — i.e. data starts at frame offset 32. Write replies stop
        // after the error code (no data).
        var data = command == CmdReadResponse && frame.Length > 32
            ? frame[32..]
            : Memory<byte>.Empty;
        return new Response(command, error, data);
    }

    public static ushort[] WordsFromLeBytes(ReadOnlySpan<byte> data, int count)
    {
        if (data.Length < count * 2)
            throw new InvalidDataException(
                $"FEnet response carries {data.Length} byte(s) but {count * 2} are required for {count} words.");

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

    private static byte[] Wrap(byte[] instruction, ushort invokeId, byte cpuInfo)
    {
        var frame = new byte[20 + instruction.Length];
        "LSIS-XGT\0\0"u8.CopyTo(frame.AsSpan(0));
        frame[12] = cpuInfo;
        frame[13] = 0x33; // PC → PLC
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(14), invokeId);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(16), (ushort)instruction.Length);
        frame[18] = 0x00; // FEnet position: slot/base 0

        byte bcc = 0;
        for (int i = 0; i < 19; i++)
            bcc += frame[i];
        frame[19] = bcc;

        instruction.CopyTo(frame, 20);
        return frame;
    }

    private static void WriteInstructionHeader(MemoryStream stream, ushort command, ushort dataType)
    {
        WriteU16(stream, command);
        WriteU16(stream, dataType);
        WriteU16(stream, 0); // reserved
    }

    private static void WriteVariable(MemoryStream stream, string variable)
    {
        var bytes = Encoding.ASCII.GetBytes(variable);
        WriteU16(stream, (ushort)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteU16(MemoryStream stream, ushort value) =>
        stream.Write([(byte)(value & 0xFF), (byte)(value >> 8)]);
}

/// <summary>FEnet error status != 0 — the PLC rejected the request.</summary>
internal sealed class LsFEnetStatusException : Exception
{
    public ushort Code { get; }

    public LsFEnetStatusException(ushort code)
        : base($"The PLC answered with FEnet error 0x{code:X4} ({Describe(code)}).")
    {
        Code = code;
    }

    private static string Describe(ushort code) => code switch
    {
        0x0001 => "more than 16 blocks in an individual read/write",
        0x0002 => "data type other than X/B/W/D/L",
        0x0003 => "device not in service for this CPU",
        0x0004 => "address outside the supported device range",
        0x0005 => "single block exceeds the 1400-byte limit",
        0x0006 => "request exceeds the total 1400-byte limit",
        0x0075 => "invalid company ID",
        0x0076 => "invalid frame length",
        0x0077 => "invalid header checksum (BCC)",
        0x0078 => "unknown service command",
        _ => "unknown error",
    };
}
