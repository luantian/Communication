using System.Buffers.Binary;
using System.Net.Sockets;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication.LsFEnet;

/// <summary>
/// LS Electric XGB/XGK dedicated-protocol client over FEnet TCP 2004, implemented in-library.
/// Word access uses continuous reads/writes (%PW/%MW/... variables); bit access uses individual
/// reads/writes on %MX absolute bit variables (≤16 per request — continuous mode forbids BIT).
/// </summary>
public sealed class LsFEnetClient : PlcClientBase
{
    private readonly LsFEnetOptions _options;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _invokeId;

    public LsFEnetClient(LsFEnetOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        _options = options;
    }

    protected override async Task DoConnectAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(_options.Ip, _options.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    protected override Task DoDisconnectAsync()
    {
        var stream = _stream;
        var tcp = _tcp;
        _stream = null;
        _tcp = null;

        stream?.Dispose();
        tcp?.Dispose();
        return Task.CompletedTask;
    }

    protected override DeviceAddress ParseAddress(string address) => LsFEnetAddress.Parse(address);

    protected override async Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        try
        {
            var (variable, isBit) = LsFEnetAddress.ParseVariable(address.Raw ?? throw new FormatException("Address lost its raw text."));
            if (!isBit)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"'{address.Raw}' is a word address; use the word.bit form (e.g. M100.3) for bit reads.");
            if (count > LsFEnetFrame.MaxVariablesPerRequest)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Individual BIT reads are limited to {LsFEnetFrame.MaxVariablesPerRequest} points per request.");

            var variables = Enumerable.Range(0, count).Select(i => BitOffset(variable, i)).ToArray();
            var request = LsFEnetFrame.BuildIndividualReadBits(Next(), _options.CpuInfo, variables);
            var response = await TransactAsync(request, ct).ConfigureAwait(false);

            var bits = new bool[count];
            for (int i = 0; i < count; i++)
                bits[i] = response.Data.Span[i] == 1;
            return CommResult<bool[]>.Ok(bits);
        }
        catch (LsFEnetStatusException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.DeviceRejected, $"0x{ex.Code:X4}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
        catch (FormatException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.InvalidAddress, null, ex.Message);
        }
    }

    protected override async Task<CommResult> DoWriteBitsAsync(DeviceAddress address, IReadOnlyList<bool> values, CancellationToken ct)
    {
        try
        {
            var (variable, isBit) = LsFEnetAddress.ParseVariable(address.Raw ?? throw new FormatException("Address lost its raw text."));
            if (!isBit)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"'{address.Raw}' is a word address; use the word.bit form (e.g. M100.3) for bit writes.");
            if (values.Count > LsFEnetFrame.MaxVariablesPerRequest)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Individual BIT writes are limited to {LsFEnetFrame.MaxVariablesPerRequest} points per request.");

            var items = values.Select((on, i) => (BitOffset(variable, i), on)).ToArray();
            var request = LsFEnetFrame.BuildIndividualWriteBits(Next(), _options.CpuInfo, items);
            await TransactAsync(request, ct).ConfigureAwait(false);
            return CommResult.Ok();
        }
        catch (LsFEnetStatusException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, $"0x{ex.Code:X4}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
        catch (FormatException ex)
        {
            return CommResult.Fail(CommErrorKind.InvalidAddress, null, ex.Message);
        }
    }

    protected override async Task<CommResult<ushort[]>> DoReadWordsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        try
        {
            var (variable, isBit) = LsFEnetAddress.ParseVariable(address.Raw ?? throw new FormatException("Address lost its raw text."));
            if (isBit)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"'{address.Raw}' is a bit address; use the word form (e.g. M100).");
            var byteCount = checked((ushort)(count * 2));
            if (byteCount > LsFEnetFrame.MaxBytesPerRequest)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Reading {count} words exceeds the FEnet limit of {LsFEnetFrame.MaxBytesPerRequest / 2} words.");

            var request = LsFEnetFrame.BuildContinuousRead(Next(), _options.CpuInfo, variable, byteCount);
            var response = await TransactAsync(request, ct).ConfigureAwait(false);

            return CommResult<ushort[]>.Ok(LsFEnetFrame.WordsFromLeBytes(response.Data.Span, count));
        }
        catch (LsFEnetStatusException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.DeviceRejected, $"0x{ex.Code:X4}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
        catch (FormatException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.InvalidAddress, null, ex.Message);
        }
    }

    protected override async Task<CommResult> DoWriteWordsAsync(DeviceAddress address, IReadOnlyList<ushort> values, CancellationToken ct)
    {
        try
        {
            var (variable, isBit) = LsFEnetAddress.ParseVariable(address.Raw ?? throw new FormatException("Address lost its raw text."));
            if (isBit)
                return CommResult.Fail(CommErrorKind.InvalidAddress, null,
                    $"'{address.Raw}' is a bit address; use the word form (e.g. M100).");
            if (values.Count * 2 > LsFEnetFrame.MaxBytesPerRequest)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Writing {values.Count} words exceeds the FEnet limit of {LsFEnetFrame.MaxBytesPerRequest / 2} words.");

            var request = LsFEnetFrame.BuildIndividualWrite(Next(), _options.CpuInfo,
                [(variable, (byte[])LsFEnetFrame.WordsToLeBytes(values))]);
            await TransactAsync(request, ct).ConfigureAwait(false);
            return CommResult.Ok();
        }
        catch (LsFEnetStatusException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, $"0x{ex.Code:X4}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
        catch (FormatException ex)
        {
            return CommResult.Fail(CommErrorKind.InvalidAddress, null, ex.Message);
        }
    }

    protected override async Task<CommResult> DoHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            var request = LsFEnetFrame.BuildStatusRequest(Next(), _options.CpuInfo);
            await TransactAsync(request, ct).ConfigureAwait(false);
            return CommResult.Ok();
        }
        catch (LsFEnetStatusException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, $"0x{ex.Code:X4}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    private static string BitOffset(string absoluteBitVariable, int offset) =>
        $"%MX{int.Parse(absoluteBitVariable[3..], System.Globalization.CultureInfo.InvariantCulture) + offset}";

    private async Task<LsFEnetFrame.Response> TransactAsync(byte[] request, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var header = new byte[20];
        await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
        if (!header.AsSpan(..8).SequenceEqual("LSIS-XGT"u8))
            throw new InvalidDataException("FEnet response does not carry the LSIS-XGT company ID.");


        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16));
        var full = new byte[20 + length];
        header.CopyTo(full, 0);
        await ReadExactlyAsync(stream, full.AsMemory(20), ct).ConfigureAwait(false);

        return LsFEnetFrame.ParseResponse(full, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14)));
    }

    private ushort Next() => ++_invokeId;

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(
                    $"The PLC closed the connection after {total} of {buffer.Length} expected bytes.");
            total += read;
        }
    }
}
