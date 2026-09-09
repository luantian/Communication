using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication.Mitsubishi;

/// <summary>
/// MC-protocol primitives shared by the TCP and UDP transports: device-kind checks and the four
/// Do* operations on 3E/4E binary frames built by <see cref="McFrame"/>.
/// </summary>
public abstract class McClientBase : PlcClientBase
{
    protected McClientBase(McOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        Options = options;
    }

    protected McOptions Options { get; }

    /// <summary>Sends one request frame and returns the payload after the end code; throws on transport/format errors.</summary>
    protected abstract Task<ReadOnlyMemory<byte>> TransactAsync(byte[] request, CancellationToken ct);

    protected override DeviceAddress ParseAddress(string address) => McAddress.Parse(address);

    protected override async Task<CommResult> DoHeartbeatAsync(CancellationToken ct) =>
        (await DoReadBitsAsync(McAddress.Parse("M0"), 1, ct).ConfigureAwait(false)).WithoutValue();

    protected override async Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        try
        {
            var device = McAddress.GetDevice(address.Area);
            if (device.IsWord)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a word device; use word operations.");
            if (count > McFrame.MaxBitsPerRequest)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Reading {count} bits exceeds the MC limit of {McFrame.MaxBitsPerRequest} per request.");

            var data = await RequestAsync(McFrame.CmdBatchRead, McFrame.SubCommandBit,
                address.Offset, device.Code, count, [], ct).ConfigureAwait(false);

            return CommResult<bool[]>.Ok(McFrame.UnpackBits(data.Span, count));
        }
        catch (McEndCodeException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.DeviceRejected, $"0x{ex.EndCode:X4}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    protected override async Task<CommResult> DoWriteBitsAsync(DeviceAddress address, IReadOnlyList<bool> values, CancellationToken ct)
    {
        try
        {
            var device = McAddress.GetDevice(address.Area);
            if (device.IsWord)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a word device; use word operations.");
            if (values.Count > McFrame.MaxBitsPerRequest)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Writing {values.Count} bits exceeds the MC limit of {McFrame.MaxBitsPerRequest} per request.");

            await RequestAsync(McFrame.CmdBatchWrite, McFrame.SubCommandBit,
                address.Offset, device.Code, checked((ushort)values.Count), McFrame.PackBits(values), ct)
                .ConfigureAwait(false);

            return CommResult.Ok();
        }
        catch (McEndCodeException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, $"0x{ex.EndCode:X4}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    protected override async Task<CommResult<ushort[]>> DoReadWordsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        try
        {
            var device = McAddress.GetDevice(address.Area);
            if (!device.IsWord)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a bit device; use bit operations.");
            if (count > McFrame.MaxWordsPerRequest)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Reading {count} words exceeds the MC limit of {McFrame.MaxWordsPerRequest} per request.");

            var data = await RequestAsync(McFrame.CmdBatchRead, McFrame.SubCommandWord,
                address.Offset, device.Code, count, [], ct).ConfigureAwait(false);

            return CommResult<ushort[]>.Ok(McFrame.WordsFromLeBytes(data.Span, count));
        }
        catch (McEndCodeException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.DeviceRejected, $"0x{ex.EndCode:X4}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    protected override async Task<CommResult> DoWriteWordsAsync(DeviceAddress address, IReadOnlyList<ushort> values, CancellationToken ct)
    {
        try
        {
            var device = McAddress.GetDevice(address.Area);
            if (!device.IsWord)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a bit device; use bit operations.");
            if (values.Count > McFrame.MaxWordsPerRequest)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Writing {values.Count} words exceeds the MC limit of {McFrame.MaxWordsPerRequest} per request.");

            await RequestAsync(McFrame.CmdBatchWrite, McFrame.SubCommandWord,
                address.Offset, device.Code, checked((ushort)values.Count), McFrame.WordsToLeBytes(values), ct)
                .ConfigureAwait(false);

            return CommResult.Ok();
        }
        catch (McEndCodeException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, $"0x{ex.EndCode:X4}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    private async Task<ReadOnlyMemory<byte>> RequestAsync(
        ushort command,
        ushort subCommand,
        int deviceNumber,
        byte deviceCode,
        ushort count,
        byte[] writeData,
        CancellationToken ct)
    {
        var serial = NextSerial();
        var request = McFrame.BuildRequest(
            Options.ResolveFrame(), serial, Options.NetworkNo, Options.PcNo, Options.StationNo,
            checked((ushort)Math.Min(Options.MonitorTimerMs, ushort.MaxValue)),
            command, subCommand, deviceNumber, deviceCode, count, writeData);

        var data = await TransactAsync(request, ct).ConfigureAwait(false);
        return McFrame.ParseResponse(data, Options.ResolveFrame(), serial);
    }

    private int _serial;

    protected ushort NextSerial() => (ushort)(Interlocked.Increment(ref _serial) & 0xFFFF);
}
