using System.Buffers.Binary;
using System.Net.Sockets;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication.Rockwell;

/// <summary>
/// Allen-Bradley ControlLogix / CompactLogix EtherNet/IP driver: CIP unconnected explicit messaging
/// (RegisterSession + SendRRData + Unconnected Send), tag-based addressing. Implemented in-library.
/// </summary>
public sealed partial class EipPlcClient : PlcClientBase
{
    private readonly EipOptions _options;
    private readonly ILogger _log;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private uint _sessionHandle;
    private ulong _senderContext;

    public EipPlcClient(EipOptions options, DeviceConfig device, ClientBuildContext context)
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

            await stream.WriteAsync(EipFrame.BuildRegisterSession(), ct).ConfigureAwait(false);
            var response = new byte[28];
            await ReadExactlyAsync(stream, response, ct).ConfigureAwait(false);
            _sessionHandle = EipFrame.ParseRegisterSessionResponse(response);
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
                stream.Write(EipFrame.BuildUnregisterSession(_sessionHandle));
            }
            catch (Exception ex)
            {
                LogUnregisterError(Device.Name, ex);
            }
        }

        stream?.Dispose();
        tcp?.Dispose();
        return Task.CompletedTask;
    }

    protected override DeviceAddress ParseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("Logix tag must not be empty.");

        // Validate the tag syntax; the IOI itself is rebuilt where the raw string is available.
        EipTagPath.Parse(address);
        return new DeviceAddress { Area = "TAG", Offset = 0 };
    }

    protected override async Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        try
        {
            var path = EipTagPath.Parse(address.Raw ?? throw new FormatException("Address lost its raw tag text."));
            if (count != 1)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    "Bit reads are limited to single BOOL tags or word.bit forms in this driver version.");

            if (path.HasBit)
            {
                // Read the whole element and extract the bit locally (Logix convention).
                var word = await TransactReadAsync(BuildRead(path with { Bit = -1 }, 1), EipFrame.ServiceReadTag, ct)
                    .ConfigureAwait(false);
                if (!word.Success)
                    return CommResult<bool[]>.Fail(word.Kind, word.ErrorCode, word.Message);

                if (word.Value.Data.IsEmpty)
                    return CommResult<bool[]>.Fail(CommErrorKind.ProtocolError, null, "The read returned no data.");
                return CommResult<bool[]>.Ok([ExtractBit(word.Value.Data, path.Bit)]);
            }

            var read = await TransactReadAsync(BuildRead(path, 1), EipFrame.ServiceReadTag, ct).ConfigureAwait(false);
            if (!read.Success)
                return CommResult<bool[]>.Fail(read.Kind, read.ErrorCode, read.Message);
            if (read.Value.DataType is not null and not EipFrame.TypeBool)
                return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"Tag type 0x{read.Value.DataType:X4} is not BOOL; use typed reads instead.");

            return CommResult<bool[]>.Ok([!read.Value.Data.IsEmpty && read.Value.Data.Span[0] != 0]);
        }
        catch (EipCipException ex)
        {
            return MapCip(ex, CommResult<bool[]>.Fail);
        }
        catch (EipEncapsulationException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.ProtocolError, $"0x{ex.Status:X8}", ex.Message);
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
            var path = EipTagPath.Parse(address.Raw ?? throw new FormatException("Address lost its raw tag text."));
            if (path.HasBit || values.Count != 1)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    "Bit writes are limited to single BOOL tags in this driver version (word.bit writes need read-modify-write).");

            var frame = EipFrame.BuildWriteTagRequest(_sessionHandle, ++_senderContext, _options.Slot,
                path.Ioi, EipFrame.TypeBool, 1, [(byte)(values[0] ? 1 : 0)]);
            var response = await TransactAsync(frame, ct).ConfigureAwait(false);
            _ = EipFrame.ParseCipResponse(response, EipFrame.ServiceWriteTag);
            return CommResult.Ok();
        }
        catch (EipCipException ex)
        {
            return MapCip(ex, CommResult.Fail);
        }
        catch (EipEncapsulationException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, $"0x{ex.Status:X8}", ex.Message);
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
            var path = EipTagPath.Parse(address.Raw ?? throw new FormatException("Address lost its raw tag text."));
            if (path.HasBit)
                return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                    $"'{address.Raw}' addresses a bit; use the bit API or the word form.");

            // Large reads go through Read Tag Fragmented (0x52): the controller answers with
            // partial data (status 0x06) until the requested byte count is complete.
            if (count * 2 > FragmentThresholdBytes)
                return CommResult<ushort[]>.Ok(
                    EipFrame.WordsFromLeBytes(await ReadWordsFragmentedAsync(path, count, ct).ConfigureAwait(false), count));

            var read = await TransactReadAsync(BuildRead(path, count), EipFrame.ServiceReadTag, ct).ConfigureAwait(false);
            if (!read.Success)
                return CommResult<ushort[]>.Fail(read.Kind, read.ErrorCode, read.Message);

            // UDT/structure tags return the whole structure in one element; when the caller asked
            // for more words than one element carried, pull the full structure through fragmented reads.
            if (read.Value.DataType == EipFrame.TypeStructure && read.Value.Data.Length < count * 2)
            {
                var raw = await ReadStructureAsync(path, ct).ConfigureAwait(false);
                return CommResult<ushort[]>.Ok(EipFrame.WordsFromLeBytes(raw, count));
            }

            return CommResult<ushort[]>.Ok(EipFrame.WordsFromLeBytes(read.Value.Data.Span, count));
        }
        catch (EipCipException ex)
        {
            return MapCip(ex, CommResult<ushort[]>.Fail);
        }
        catch (EipEncapsulationException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.ProtocolError, $"0x{ex.Status:X8}", ex.Message);
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
            var path = EipTagPath.Parse(address.Raw ?? throw new FormatException("Address lost its raw tag text."));
            if (path.HasBit)
                return CommResult.Fail(CommErrorKind.InvalidAddress, null,
                    $"'{address.Raw}' addresses a bit; use the word form.");

            // Discover the tag type, then write as INT elements — only INT/UINT tags accept word writes.
            var probe = await TransactReadAsync(BuildRead(path, 1), EipFrame.ServiceReadTag, ct).ConfigureAwait(false);
            if (!probe.Success)
                return probe.WithoutValue();
            if (probe.Value.DataType is not null and not EipFrame.TypeInt)
                return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                    $"Tag type 0x{probe.Value.DataType:X4} is not INT; use the typed WriteAsync<T> API instead.");

            var data = EipFrame.WordsToLeBytes(values);
            if (data.Length > FragmentThresholdBytes)
            {
                await WriteFragmentedAsync(path, EipFrame.TypeInt, checked((ushort)values.Count), data, ct).ConfigureAwait(false);
                return CommResult.Ok();
            }

            var frame = EipFrame.BuildWriteTagRequest(_sessionHandle, ++_senderContext, _options.Slot,
                path.Ioi, EipFrame.TypeInt, checked((ushort)values.Count), data);
            var response = await TransactAsync(frame, ct).ConfigureAwait(false);
            _ = EipFrame.ParseCipResponse(response, EipFrame.ServiceWriteTag);
            return CommResult.Ok();
        }
        catch (EipCipException ex)
        {
            return MapCip(ex, CommResult.Fail);
        }
        catch (EipEncapsulationException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, $"0x{ex.Status:X8}", ex.Message);
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

    /// <summary>Typed reads keep the native CIP type: BOOL/SINT/INT/DINT/LINT/REAL/LREAL map to bool/short/ushort/int/uint/long/float/double.</summary>
    public override async Task<CommResult<T>> ReadAsync<T>(string address, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ValueTypeMap.FromClrType(typeof(T));

        return await ExecuteAsync(async token =>
        {
            try
            {
                var path = EipTagPath.Parse(address);
                var target = path.HasBit ? path with { Bit = -1 } : path;
                var read = await TransactReadAsync(BuildRead(target, 1), EipFrame.ServiceReadTag, token).ConfigureAwait(false);
                if (!read.Success)
                    return CommResult<T>.Fail(read.Kind, read.ErrorCode, read.Message);

                if (path.HasBit)
                {
                    if (typeof(T) != typeof(bool))
                        return CommResult<T>.Fail(CommErrorKind.InvalidArgument, null, "Bit addresses only decode to bool.");
                    if (read.Value.Data.IsEmpty)
                        return CommResult<T>.Fail(CommErrorKind.ProtocolError, null, "The read returned no data.");
                    return CommResult<T>.Ok((T)(object)ExtractBit(read.Value.Data, path.Bit));
                }

                if (!OipTryDecodeMem<T>(read.Value.Data, out var value))
                    return CommResult<T>.Fail(CommErrorKind.InvalidArgument, null,
                        $"The tag returned {read.Value.Data.Length} byte(s); not enough for {typeof(T).Name}.");
                return CommResult<T>.Ok(value);
            }
            catch (EipCipException ex)
            {
                return MapCip(ex, CommResult<T>.Fail);
            }
            catch (EipEncapsulationException ex)
            {
                return CommResult<T>.Fail(CommErrorKind.ProtocolError, $"0x{ex.Status:X8}", ex.Message);
            }
            catch (InvalidDataException ex)
            {
                return CommResult<T>.Fail(CommErrorKind.ProtocolError, null, ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Typed writes use the CIP type matching T (bool→BOOL, short/ushort→INT, int/uint→DINT, long→LINT, float→REAL, double→LREAL).</summary>
    public override async Task<CommResult> WriteAsync<T>(string address, T value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ValueTypeMap.FromClrType(typeof(T));

        return await ExecuteAsync(async token =>
        {
            try
            {
                var path = EipTagPath.Parse(address);
                if (path.HasBit)
                    return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                        "Bit writes need read-modify-write; write the whole word instead.");

                var (type, data) = EncodeTyped(value);
                var frame = EipFrame.BuildWriteTagRequest(_sessionHandle, ++_senderContext, _options.Slot,
                    path.Ioi, type, 1, data);
                var response = await TransactAsync(frame, token).ConfigureAwait(false);
                _ = EipFrame.ParseCipResponse(response, EipFrame.ServiceWriteTag);
                return CommResult.Ok();
            }
            catch (EipCipException ex)
            {
                return MapCip(ex, CommResult.Fail);
            }
            catch (EipEncapsulationException ex)
            {
                return CommResult.Fail(CommErrorKind.ProtocolError, $"0x{ex.Status:X8}", ex.Message);
            }
            catch (InvalidDataException ex)
            {
                return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
            }
            catch (FormatException ex)
            {
                return CommResult.Fail(CommErrorKind.InvalidAddress, null, ex.Message);
            }
        }, ct).ConfigureAwait(false);
    }

    protected override async Task<CommResult> DoHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            var frame = EipFrame.BuildHeartbeatRequest(_sessionHandle, ++_senderContext, _options.Slot);
            var response = await TransactAsync(frame, ct).ConfigureAwait(false);
            _ = EipFrame.ParseCipResponse(response, EipFrame.ServiceGetAttributesAll);
            return CommResult.Ok();
        }
        catch (EipCipException ex)
        {
            return MapCip(ex, CommResult.Fail);
        }
        catch (EipEncapsulationException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, $"0x{ex.Status:X8}", ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    /// <summary>Unconnected requests should stay well under the ~500-byte CIP limit.</summary>
    private const int FragmentThresholdBytes = 400;

    /// <summary>Read Tag Fragmented (0x52) loop: status 0x06 means partial data, advance the offset and continue.</summary>
    private async Task<byte[]> ReadWordsFragmentedAsync(EipTagPath path, ushort count, CancellationToken ct)
    {
        var buffer = new byte[count * 2];
        await ReadFragmentedIntoAsync(path, count, buffer, ct).ConfigureAwait(false);
        return buffer;
    }

    /// <summary>
    /// Reads a whole UDT/structure tag as raw LE bytes (member offsets per Studio 5000 layout).
    /// The element count is 1 (the structure); chunks grow until the final status 0x00 arrives.
    /// </summary>
    public async Task<byte[]> ReadTagRawAsync(string tag, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var path = EipTagPath.Parse(tag);
        if (path.HasBit)
            throw new ArgumentException($"'{tag}' addresses a bit; raw structure reads need the tag itself.");

        var result = await ExecuteAsync(async token => CommResult<byte[]>.Ok(
            await ReadStructureAsync(path, token).ConfigureAwait(false)), ct).ConfigureAwait(false);
        return result.EnsureSuccess();
    }

    /// <summary>Fragmented read of one whole structure element; stops on the first non-partial status.</summary>
    private async Task<byte[]> ReadStructureAsync(EipTagPath path, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        uint offset = 0;
        while (true)
        {
            var frame = EipFrame.BuildReadTagFragmentedRequest(
                _sessionHandle, ++_senderContext, _options.Slot, path.Ioi, 1, offset);
            var response = await TransactAsync(frame, ct).ConfigureAwait(false);
            var parsed = EipFrame.ParseCipResponse(response, EipFrame.ServiceReadTagFragmented, allowPartial: true);

            var chunk = parsed.Data.ToArray();
            if (chunk.Length == 0)
                throw new InvalidDataException("Fragmented structure read returned an empty chunk.");

            buffer.Write(chunk);
            if (parsed.GeneralStatus != EipFrame.StatusPartialData)
                return buffer.ToArray();

            offset += (uint)chunk.Length;
        }
    }

    private async Task ReadFragmentedIntoAsync(EipTagPath path, ushort count, byte[] buffer, CancellationToken ct)
    {
        uint offset = 0;
        while (offset < buffer.Length)
        {
            var frame = EipFrame.BuildReadTagFragmentedRequest(
                _sessionHandle, ++_senderContext, _options.Slot, path.Ioi, count, offset);
            var response = await TransactAsync(frame, ct).ConfigureAwait(false);
            var parsed = EipFrame.ParseCipResponse(response, EipFrame.ServiceReadTagFragmented, allowPartial: true);

            var chunk = parsed.Data.ToArray();
            if (chunk.Length == 0)
                throw new InvalidDataException("Fragmented read returned an empty chunk.");
            if (offset + chunk.Length > buffer.Length)
                throw new InvalidDataException(
                    $"Fragmented read delivered {offset + chunk.Length} bytes; only {buffer.Length} were requested.");

            chunk.CopyTo(buffer, (int)offset);
            offset += (uint)chunk.Length;
        }
    }

    /// <summary>Write Tag Fragmented (0x53): chunks of at most FragmentThresholdBytes with advancing byte offsets.</summary>
    private async Task WriteFragmentedAsync(EipTagPath path, ushort typeCode, ushort elementCount, byte[] data, CancellationToken ct)
    {
        uint offset = 0;
        while (offset < data.Length)
        {
            var take = (int)Math.Min(FragmentThresholdBytes, data.Length - offset);
            var frame = EipFrame.BuildWriteTagFragmentedRequest(
                _sessionHandle, ++_senderContext, _options.Slot, path.Ioi, typeCode, elementCount,
                offset, data.AsSpan((int)offset, take));
            var response = await TransactAsync(frame, ct).ConfigureAwait(false);
            _ = EipFrame.ParseCipResponse(response, EipFrame.ServiceWriteTagFragmented);
            offset += (uint)take;
        }
    }

    private byte[] BuildRead(EipTagPath path, ushort count) =>
        EipFrame.BuildReadTagRequest(_sessionHandle, ++_senderContext, _options.Slot, path.Ioi, count);

    private async Task<CommResult<EipFrame.CipResponse>> TransactReadAsync(byte[] frame, byte service, CancellationToken ct)
    {
        var response = await TransactAsync(frame, ct).ConfigureAwait(false);
        return CommResult<EipFrame.CipResponse>.Ok(EipFrame.ParseCipResponse(response, service));
    }

    private async Task<ReadOnlyMemory<byte>> TransactAsync(byte[] frame, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");

        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var header = new byte[24];
        await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));

        var full = new byte[24 + length];
        header.CopyTo(full, 0);
        await ReadExactlyAsync(stream, full.AsMemory(24), ct).ConfigureAwait(false);
        return full;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Device}] error while unregistering the session")]
    private partial void LogUnregisterError(string device, Exception ex);

    private static bool ExtractBit(ReadOnlyMemory<byte> data, int bit) =>
        !data.Span.IsEmpty && (data.Span[bit / 8] & (1 << (bit % 8))) != 0;

    private static bool OipTryDecodeMem<T>(ReadOnlyMemory<byte> data, out T value) where T : struct =>
        OipTryDecode(data.Span, out value);

    private static bool OipTryDecode<T>(ReadOnlySpan<byte> data, out T value) where T : struct
    {
        switch (typeof(T))
        {
            case var t when t == typeof(bool):
                if (data.Length < 1) break;
                value = (T)(object)(data[0] != 0);
                return true;
            case var t when t == typeof(short) || t == typeof(ushort):
                if (data.Length < 2) break;
                var w = BinaryPrimitives.ReadUInt16LittleEndian(data);
                value = typeof(T) == typeof(short) ? (T)(object)unchecked((short)w) : (T)(object)w;
                return true;
            case var t when t == typeof(int) || t == typeof(uint) || t == typeof(float):
                if (data.Length < 4) break;
                var d = BinaryPrimitives.ReadUInt32LittleEndian(data);
                if (typeof(T) == typeof(int)) value = (T)(object)unchecked((int)d);
                else if (typeof(T) == typeof(uint)) value = (T)(object)d;
                else value = (T)(object)BitConverter.UInt32BitsToSingle(d);
                return true;
            case var t when t == typeof(long) || t == typeof(double):
                if (data.Length < 8) break;
                var l = BinaryPrimitives.ReadUInt64LittleEndian(data);
                value = typeof(T) == typeof(long) ? (T)(object)unchecked((long)l) : (T)(object)BitConverter.UInt64BitsToDouble(l);
                return true;
        }

        value = default;
        return false;
    }

    private static (ushort Type, byte[] Data) EncodeTyped<T>(T value) where T : struct
    {
        if (typeof(T) == typeof(bool)) return (EipFrame.TypeBool, [(bool)(object)value ? (byte)1 : (byte)0]);
        if (typeof(T) == typeof(short)) return (EipFrame.TypeInt, [.. BitConverter.GetBytes((short)(object)value)]);
        if (typeof(T) == typeof(ushort)) return (EipFrame.TypeInt, [.. BitConverter.GetBytes((ushort)(object)value)]);
        if (typeof(T) == typeof(int)) return (EipFrame.TypeDint, [.. BitConverter.GetBytes((int)(object)value)]);
        if (typeof(T) == typeof(uint)) return (EipFrame.TypeDint, [.. BitConverter.GetBytes((uint)(object)value)]);
        if (typeof(T) == typeof(long)) return (EipFrame.TypeLint, [.. BitConverter.GetBytes((long)(object)value)]);
        if (typeof(T) == typeof(float)) return (EipFrame.TypeReal, [.. BitConverter.GetBytes((float)(object)value)]);
        if (typeof(T) == typeof(double)) return (EipFrame.TypeLreal, [.. BitConverter.GetBytes((double)(object)value)]);
        throw new NotSupportedException($"Type {typeof(T).Name} is not supported by the EtherNet/IP driver.");
    }

    private static CommErrorKind MapCipKind(EipCipException ex) => ex.Status switch
    {
        0x04 or 0x05 or 0x26 => CommErrorKind.InvalidAddress,
        0x06 or 0x13 or 0x15 or 0x20 => CommErrorKind.InvalidArgument,
        _ => CommErrorKind.DeviceRejected,
    };

    private static T MapCip<T>(EipCipException ex, Func<CommErrorKind, string?, string?, T> fail) =>
        fail(MapCipKind(ex), $"0x{ex.Status:X2}", ex.Message);

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(
                    $"The controller closed the connection after {total} of {buffer.Length} expected bytes.");
            total += read;
        }
    }
}
