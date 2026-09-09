using System.Net.Sockets;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication.GeSrtp;

/// <summary>
/// GE (GE Fanuc / Emerson) Series 90-30 / 90-70 / RX3i SRTP client over TCP 18245, implemented
/// in-library from the reverse-engineered specification. One memory type per transaction; the
/// connection performs the INIT handshake and every request carries an echoed sequence number.
/// </summary>
public sealed partial class GeSrtpClient : PlcClientBase
{
    private readonly GeSrtpOptions _options;
    private readonly ILogger _log;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private byte _sequence;

    public GeSrtpClient(GeSrtpOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        _options = options;
        _log = Logger;
    }

    protected override async Task DoConnectAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        NetworkStream? stream = null;
        try
        {
            await tcp.ConnectAsync(_options.Ip, _options.Port, ct).ConfigureAwait(false);
            stream = tcp.GetStream();

            // INIT handshake: 56 zero bytes, the PLC answers with 0x01 in byte 0.
            await stream.WriteAsync(GeSrtpFrame.BuildInitFrame(), ct).ConfigureAwait(false);
            var init = new byte[56];
            await ReadExactlyAsync(stream, init, ct).ConfigureAwait(false);
            _ = GeSrtpFrame.ParseInitResponse(init);
        }
        catch
        {
            stream?.Dispose();
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = stream;
    }

    protected override Task DoDisconnectAsync()
    {
        var stream = _stream;
        var tcp = _tcp;
        _stream = null;
        _tcp = null;

        if (stream is not null)
        {
            try
            {
                // Polite close: repeat the INIT frame before dropping the socket.
                stream.Write(GeSrtpFrame.BuildInitFrame());
            }
            catch (Exception ex)
            {
                LogCloseError(Device.Name, ex);
            }
        }

        stream?.Dispose();
        tcp?.Dispose();
        return Task.CompletedTask;
    }

    protected override DeviceAddress ParseAddress(string address) => GeSrtpAddress.Parse(address);

    protected override async Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        try
        {
            var memory = GeSrtpAddress.GetMemory(address.Area);
            if (memory.IsWord)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Memory {address.Area} is a word memory; use word operations.");

            // One transaction per memory type; SRTP returns bytes, unpack as bits LSB-first.
            var result = await TransactAsync(GeSrtpFrame.BuildShortRead(Next(), memory.Selector, address.Offset, count), null, ct)
                .ConfigureAwait(false);

            return CommResult<bool[]>.Ok(UnpackBits(result));
        }
        catch (GeSrtpStatusException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.DeviceRejected, $"0x{ex.Primary:X2}/0x{ex.Secondary:X2}", ex.Message);
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
            var memory = GeSrtpAddress.GetMemory(address.Area);
            if (memory.IsWord)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Memory {address.Area} is a word memory; use word operations.");

            var data = new byte[values.Count];
            for (int i = 0; i < values.Count; i++)
                data[i] = values[i] ? (byte)0xFF : (byte)0x00;

            await TransactAsync(
                GeSrtpFrame.BuildShortWrite(Next(), memory.Selector, address.Offset, checked((ushort)values.Count), data), null, ct)
                .ConfigureAwait(false);
            return CommResult.Ok();
        }
        catch (GeSrtpStatusException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, $"0x{ex.Primary:X2}/0x{ex.Secondary:X2}", ex.Message);
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
            var memory = GeSrtpAddress.GetMemory(address.Area);
            if (!memory.IsWord)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Memory {address.Area} is a bit memory; use bit operations.");
            if (count > GeSrtpFrame.MaxWordsPerRequest)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Reading {count} words exceeds the SRTP limit of {GeSrtpFrame.MaxWordsPerRequest}.");

            var response = await TransactAsync(
                GeSrtpFrame.BuildShortRead(Next(), memory.Selector, address.Offset, count),
                (ushort)(count * 2), ct).ConfigureAwait(false);

            return CommResult<ushort[]>.Ok(GeSrtpFrame.WordsFromLeBytes(response, count));
        }
        catch (GeSrtpStatusException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.DeviceRejected, $"0x{ex.Primary:X2}/0x{ex.Secondary:X2}", ex.Message);
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
            var memory = GeSrtpAddress.GetMemory(address.Area);
            if (!memory.IsWord)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Memory {address.Area} is a bit memory; use bit operations.");
            if (values.Count > GeSrtpFrame.MaxWordsPerRequest)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Writing {values.Count} words exceeds the SRTP limit of {GeSrtpFrame.MaxWordsPerRequest}.");

            var data = GeSrtpFrame.WordsToLeBytes(values);
            var request = data.Length <= GeSrtpFrame.ShortInlineDataBytes
                ? GeSrtpFrame.BuildShortWrite(Next(), memory.Selector, address.Offset, checked((ushort)values.Count), data)
                : GeSrtpFrame.BuildExtendedWrite(Next(), memory.Selector, address.Offset, checked((ushort)values.Count), data);

            await TransactAsync(request, null, ct).ConfigureAwait(false);
            return CommResult.Ok();
        }
        catch (GeSrtpStatusException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, $"0x{ex.Primary:X2}/0x{ex.Secondary:X2}", ex.Message);
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

    protected override async Task<CommResult> DoHeartbeatAsync(CancellationToken ct) =>
        (await DoReadWordsAsync(new DeviceAddress { Area = "R", Offset = 1 }, 1, ct).ConfigureAwait(false)).WithoutValue();

    /// <summary>Sends one request; when expectedDataBytes is set, a 0x94 reply's follow-up data frame is read and returned.</summary>
    private async Task<byte[]> TransactAsync(byte[] request, ushort? expectedDataBytes, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var header = new byte[56];
        await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
        var response = GeSrtpFrame.ParseResponse(header, request[2]);

        if (response.Kind == GeSrtpFrame.ResponseKind.WithBuffer)
        {
            var length = expectedDataBytes ?? response.BufferedBytes;
            var data = new byte[length];
            await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false);
            return data;
        }

        return response.Data.ToArray();
    }

    private static bool[] UnpackBits(byte[] data)
    {
        var bits = new bool[data.Length * 8];
        for (int i = 0; i < bits.Length; i++)
            bits[i] = (data[i / 8] & (1 << (i % 8))) != 0;
        return bits;
    }

    private byte Next() => ++_sequence;

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Device}] error while closing the SRTP connection")]
    private partial void LogCloseError(string device, Exception ex);

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(
                    $"The PLC closed the connection after {total} of {buffer.Length} expected bytes.");
            total += read;
        }
    }
}
