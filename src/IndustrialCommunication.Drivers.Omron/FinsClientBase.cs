using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication.Omron;

/// <summary>
/// FINS primitives shared by the TCP and UDP transports: area checks and the four Do* operations
/// building bare FINS frames with <see cref="FinsFrame"/>.
/// </summary>
public abstract class FinsClientBase : PlcClientBase
{
    protected FinsClientBase(FinsOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        Options = options;
        SourceNode = options.SourceNode;
        DestNode = options.ResolveDestNode();
    }

    protected FinsOptions Options { get; }

    // Effective FINS node addresses; the TCP transport refines them through the node handshake.
    protected byte SourceNode { get; set; }
    protected byte DestNode { get; set; }

    /// <summary>Sends one bare FINS frame and returns the bare response frame; throws on transport/format errors.</summary>
    protected abstract Task<ReadOnlyMemory<byte>> TransactAsync(ReadOnlyMemory<byte> finsFrame, CancellationToken ct);

    protected override DeviceAddress ParseAddress(string address) => FinsAddress.Parse(address);

    protected override async Task<CommResult> DoHeartbeatAsync(CancellationToken ct) =>
        (await DoReadBitsAsync(FinsAddress.Parse("CIO0.0"), 1, ct).ConfigureAwait(false)).WithoutValue();

    protected override async Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        try
        {
            var area = FinsAddress.GetArea(address.Area);
            if (!address.IsBit)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"FINS bit reads need a word.bit address (e.g. CIO100.5); '{address.Area}{address.Offset}' has no bit number.");
            if (count > FinsFrame.MaxBitsPerRequest)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Reading {count} bits exceeds the FINS limit of {FinsFrame.MaxBitsPerRequest} per request.");

            var data = await RequestAsync(FinsFrame.CmdMemoryAreaRead, area.BitCode,
                address.Offset, checked((byte)address.Bit), count, [], ct).ConfigureAwait(false);

            return CommResult<bool[]>.Ok(FinsFrame.BitsFromBytes(data.Span, count));
        }
        catch (FinsResponseCodeException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.DeviceRejected, $"0x{ex.ResponseCode:X4}", ex.Message);
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
            var area = FinsAddress.GetArea(address.Area);
            if (!address.IsBit)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"FINS bit writes need a word.bit address (e.g. CIO100.5); '{address.Area}{address.Offset}' has no bit number.");

            await RequestAsync(FinsFrame.CmdMemoryAreaWrite, area.BitCode,
                address.Offset, checked((byte)address.Bit), checked((ushort)values.Count),
                FinsFrame.BitsToBytes(values), ct).ConfigureAwait(false);

            return CommResult.Ok();
        }
        catch (FinsResponseCodeException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, $"0x{ex.ResponseCode:X4}", ex.Message);
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
            var area = FinsAddress.GetArea(address.Area);
            if (address.IsBit)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"'{address.Area}{address.Offset}.{address.Bit}' is a bit address; use word form like {address.Area}{address.Offset}.");
            if (count > FinsFrame.MaxWordsPerRequest)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Reading {count} words exceeds the FINS limit of {FinsFrame.MaxWordsPerRequest} per request.");

            var data = await RequestAsync(FinsFrame.CmdMemoryAreaRead, area.WordCode,
                address.Offset, 0, count, [], ct).ConfigureAwait(false);

            return CommResult<ushort[]>.Ok(FinsFrame.WordsFromBeBytes(data.Span, count));
        }
        catch (FinsResponseCodeException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.DeviceRejected, $"0x{ex.ResponseCode:X4}", ex.Message);
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
            var area = FinsAddress.GetArea(address.Area);
            if (address.IsBit)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"'{address.Area}{address.Offset}.{address.Bit}' is a bit address; use word form like {address.Area}{address.Offset}.");
            if (values.Count > FinsFrame.MaxWordsPerRequest)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Writing {values.Count} words exceeds the FINS limit of {FinsFrame.MaxWordsPerRequest} per request.");

            await RequestAsync(FinsFrame.CmdMemoryAreaWrite, area.WordCode,
                address.Offset, 0, checked((ushort)values.Count), FinsFrame.WordsToBeBytes(values), ct)
                .ConfigureAwait(false);

            return CommResult.Ok();
        }
        catch (FinsResponseCodeException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, $"0x{ex.ResponseCode:X4}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    private async Task<ReadOnlyMemory<byte>> RequestAsync(
        ushort command,
        ushort areaCode,
        int wordAddress,
        byte bitAddress,
        ushort count,
        byte[] writeData,
        CancellationToken ct)
    {
        var sid = NextSid();
        var frame = FinsFrame.BuildFinsCommand(
            Options.SourceNet, SourceNode, Options.DestNet, DestNode,
            sid, command, areaCode, wordAddress, bitAddress, count, writeData);

        var response = await TransactAsync(frame, ct).ConfigureAwait(false);
        return FinsFrame.ParseFinsResponse(response, sid);
    }

    private byte _sid;

    protected byte NextSid() => ++_sid;
}
