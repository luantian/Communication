using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication.Keyence;

/// <summary>Upper-computer-link primitives shared by the TCP and UDP transports.</summary>
public abstract class KeyenceClientBase : PlcClientBase
{
    protected KeyenceClientBase(KeyenceOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        Options = options;
    }

    protected KeyenceOptions Options { get; }

    /// <summary>Sends one command line and returns the response line (without terminators).</summary>
    protected abstract Task<string> TransactAsync(string command, CancellationToken ct);

    protected override DeviceAddress ParseAddress(string address) => KeyenceAddress.Parse(address);

    protected override async Task<CommResult> DoHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            // ?E asks for the CPU error number; any well-formed answer proves the link is alive.
            var line = await TransactAsync(KeyenceFrame.BuildErrorStatusProbe(), ct).ConfigureAwait(false);
            _ = KeyenceFrame.ParseTokens(line);
            return CommResult.Ok();
        }
        catch (KeyenceErrorException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, ex.Code, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    protected override async Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        try
        {
            var device = KeyenceAddress.GetDevice(address.Area);
            if (device.IsWord)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a word device; use word operations.");
            if (count > KeyenceFrame.MaxPointsPerRequest)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Reading {count} bits exceeds the upper-link limit of {KeyenceFrame.MaxPointsPerRequest}.");

            var line = await TransactAsync(KeyenceFrame.BuildReadBits(address.Area, address.Offset, count), ct)
                .ConfigureAwait(false);
            return CommResult<bool[]>.Ok(KeyenceFrame.ParseBits(line, count));
        }
        catch (KeyenceErrorException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.DeviceRejected, ex.Code, ex.Message);
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
            var device = KeyenceAddress.GetDevice(address.Area);
            if (device.IsWord)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a word device; use word operations.");
            if (values.Count > KeyenceFrame.MaxPointsPerRequest)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Writing {values.Count} bits exceeds the upper-link limit of {KeyenceFrame.MaxPointsPerRequest}.");

            var line = await TransactAsync(KeyenceFrame.BuildWriteBits(address.Area, address.Offset, values), ct)
                .ConfigureAwait(false);
            ExpectOk(line);
            return CommResult.Ok();
        }
        catch (KeyenceErrorException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, ex.Code, ex.Message);
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
            var device = KeyenceAddress.GetDevice(address.Area);
            if (!device.IsWord)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a bit device; use bit operations.");
            if (count > KeyenceFrame.MaxPointsPerRequest)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Reading {count} words exceeds the upper-link limit of {KeyenceFrame.MaxPointsPerRequest}.");

            var line = await TransactAsync(KeyenceFrame.BuildReadWords(address.Area, address.Offset, count), ct)
                .ConfigureAwait(false);
            return CommResult<ushort[]>.Ok(KeyenceFrame.ParseWords(line, count));
        }
        catch (KeyenceErrorException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.DeviceRejected, ex.Code, ex.Message);
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
            var device = KeyenceAddress.GetDevice(address.Area);
            if (!device.IsWord)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Device {address.Area} is a bit device; use bit operations.");
            if (values.Count > KeyenceFrame.MaxPointsPerRequest)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Writing {values.Count} words exceeds the upper-link limit of {KeyenceFrame.MaxPointsPerRequest}.");

            var line = await TransactAsync(KeyenceFrame.BuildWriteWords(address.Area, address.Offset, values), ct)
                .ConfigureAwait(false);
            ExpectOk(line);
            return CommResult.Ok();
        }
        catch (KeyenceErrorException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected, ex.Code, ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    private static void ExpectOk(string line)
    {
        if (!string.Equals(KeyenceFrame.ParseTokens(line).FirstOrDefault(), "OK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Expected 'OK' from the PLC but received '{line}'.");
    }
}
