using System.Net.Sockets;
using System.Text;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication.Panasonic;

/// <summary>
/// Panasonic FP series MEWTOCOL-COM client over TCP (port 9094 on Panasonic communication units),
/// implemented in-library. Single-bit access uses RCS/WCS; word access uses RDD/WDD.
/// </summary>
public sealed class MewtocolClient : PlcClientBase
{
    private readonly MewtocolOptions _options;
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public MewtocolClient(MewtocolOptions options, DeviceConfig device, ClientBuildContext context)
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

    protected override DeviceAddress ParseAddress(string address) => MewtocolAddress.Parse(address);

    protected override async Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        try
        {
            var device = MewtocolAddress.GetDevice(address.Area);
            if (device.IsWord)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a word device; use word operations.");
            if (count != 1)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    "Mewtocol bit reads are single-point (RCS); use word reads for bit patterns.");

            var line = await TransactAsync(
                MewtocolFrame.BuildReadContact(_options.Station, device.AreaCode, address.Offset, address.Bit), ct)
                .ConfigureAwait(false);
            var (_, data) = MewtocolFrame.ParseResponse(line);
            if (data is not "0" and not "1")
                throw new InvalidDataException($"Mewtocol RCS reply data '{data}' is not a bit.");
            return CommResult<bool[]>.Ok([data == "1"]);
        }
        catch (MewtocolErrorException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.DeviceRejected, ex.Code, ex.Message);
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
            var device = MewtocolAddress.GetDevice(address.Area);
            if (device.IsWord)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a word device; use word operations.");
            if (values.Count != 1)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    "Mewtocol bit writes are single-point (WCS); use word writes for bit patterns.");

            var line = await TransactAsync(
                MewtocolFrame.BuildWriteContact(_options.Station, device.AreaCode, address.Offset, address.Bit, values[0]), ct)
                .ConfigureAwait(false);
            _ = MewtocolFrame.ParseResponse(line);
            return CommResult.Ok();
        }
        catch (MewtocolErrorException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, ex.Code, ex.Message);
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
            var device = MewtocolAddress.GetDevice(address.Area);
            if (!device.IsWord)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a bit device; use bit operations.");
            if (count > MewtocolFrame.MaxWordsPerRequest)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Reading {count} words exceeds the standard-frame limit of {MewtocolFrame.MaxWordsPerRequest}.");

            var start = address.Offset;
            var data = await ReadWordsWithContinuationAsync(
                MewtocolFrame.BuildReadWords(_options.Station, start, start + count - 1), count, ct)
                .ConfigureAwait(false);
            return CommResult<ushort[]>.Ok(MewtocolFrame.ParseWords(data, count));
        }
        catch (MewtocolErrorException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.DeviceRejected, ex.Code, ex.Message);
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
            var device = MewtocolAddress.GetDevice(address.Area);
            if (!device.IsWord)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a bit device; use bit operations.");
            if (values.Count > MewtocolFrame.MaxWordsPerRequest)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Writing {values.Count} words exceeds the standard-frame limit of {MewtocolFrame.MaxWordsPerRequest}.");

            var start = address.Offset;
            var line = await TransactAsync(
                MewtocolFrame.BuildWriteWords(_options.Station, start, start + values.Count - 1, values), ct)
                .ConfigureAwait(false);
            _ = MewtocolFrame.ParseResponse(line);
            return CommResult.Ok();
        }
        catch (MewtocolErrorException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, ex.Code, ex.Message);
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
            // #RT asks for the PLC status; any well-formed answer proves the link is alive.
            var line = await TransactAsync(MewtocolFrame.BuildStatusProbe(_options.Station), ct).ConfigureAwait(false);
            _ = MewtocolFrame.ParseResponse(line);
            return CommResult.Ok();
        }
        catch (MewtocolErrorException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, ex.Code, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    /// <summary>
    /// Reads a word range with multi-frame continuation: when a response segment ends with '&amp;'
    /// the client sends "%station**&amp;\r" until the final segment arrives, then reassembles the payloads.
    /// </summary>
    private async Task<string> ReadWordsWithContinuationAsync(string readCommand, int expectedWords, CancellationToken ct)
    {
        var data = new System.Text.StringBuilder();
        var command = readCommand;
        while (true)
        {
            var line = await TransactAsync(command, ct).ConfigureAwait(false);
            var (_, payload) = MewtocolFrame.ParseResponse(line, out var hasMore);
            data.Append(payload);
            if (!hasMore)
                return data.ToString();
            if (data.Length >= expectedWords * 4)
                throw new InvalidDataException("Mewtocol multi-frame response exceeded the requested word count.");
            command = MewtocolFrame.BuildContinuationRequest(_options.Station);
        }
    }

    private async Task<string> TransactAsync(string command, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");

        var bytes = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        // Responses end with CR; stray NUL/LF bytes (seen on some units) are skipped.
        var line = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("The PLC closed the connection.");

            var b = buffer[0];
            if (b == 0x0D)
            {
                if (line.Length == 0)
                    continue;
                return line.ToString();
            }
            if (b is 0x00 or 0x0A)
                continue;
            line.Append((char)b);
        }
    }
}
