using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;
using S7.Net;
using S7.Net.Types;

namespace IndustrialCommunication.Siemens;

/// <summary>Siemens S7 driver built on S7netplus. Words are transmitted big-endian as the S7 protocol does.</summary>
public sealed class S7PlcClient : PlcClientBase
{
    private readonly S7Options _options;
    private Plc? _plc;

    public S7PlcClient(S7Options options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, DataLayout.ABCD, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        _options = options;
    }

    protected override async Task DoConnectAsync(CancellationToken ct)
    {
        var plc = new Plc(_options.ResolveCpuType(), _options.Ip, _options.Port, _options.Rack, _options.Slot)
        {
            // Fire the in-driver timeouts slightly before the base-class operation timeout
            // so a stuck response surfaces as a timeout instead of hanging the await.
            ReadTimeout = Math.Max(500, Runtime.TimeoutMs - 200),
            WriteTimeout = Math.Max(500, Runtime.TimeoutMs - 200),
        };

        try
        {
            await plc.OpenAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            plc.Close();
            throw;
        }

        _plc = plc;
    }

    protected override Task DoDisconnectAsync()
    {
        var plc = _plc;
        _plc = null;
        plc?.Close();
        return Task.CompletedTask;
    }

    protected override DeviceAddress ParseAddress(string address) => S7Address.Parse(address);

    protected override async Task<CommResult> DoHeartbeatAsync(CancellationToken ct) =>
        (await DoReadBitsAsync(S7Address.Parse("M0.0"), 1, ct).ConfigureAwait(false)).WithoutValue();

    protected override async Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        var plc = RequirePlc();
        try
        {
            var bytes = await plc.ReadBytesAsync(
                ToS7DataType(address.Area), address.DbNo, address.Offset, (address.Bit + count + 7) / 8, ct)
                .ConfigureAwait(false);

            var bits = new bool[count];
            for (int i = 0; i < count; i++)
            {
                var bitIndex = address.Bit + i;
                bits[i] = (bytes[bitIndex / 8] & (1 << (bitIndex % 8))) != 0;
            }
            return CommResult<bool[]>.Ok(bits);
        }
        catch (PlcException ex)
        {
            return CommResult<bool[]>.Fail(MapError(ex.ErrorCode), ex.ErrorCode.ToString(), ex.Message);
        }
        catch (S7.Net.InvalidDataException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    protected override async Task<CommResult> DoWriteBitsAsync(DeviceAddress address, IReadOnlyList<bool> values, CancellationToken ct)
    {
        var plc = RequirePlc();
        try
        {
            for (int i = 0; i < values.Count; i++)
            {
                var bitIndex = address.Bit + i;
                await plc.WriteBitAsync(
                    ToS7DataType(address.Area), address.DbNo,
                    address.Offset + bitIndex / 8, bitIndex % 8, values[i], ct)
                    .ConfigureAwait(false);
            }
            return CommResult.Ok();
        }
        catch (PlcException ex)
        {
            return CommResult.Fail(MapError(ex.ErrorCode), ex.ErrorCode.ToString(), ex.Message);
        }
        catch (S7.Net.InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    protected override async Task<CommResult<ushort[]>> DoReadWordsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        var plc = RequirePlc();
        try
        {
            // Timer/Counter cells are 2 bytes each: T5 → byte offset 10; "count" counts T/C points.
            var (byteOffset, byteLength) = address.Area is "T" or "C"
                ? (address.Offset * 2, count * 2)
                : (address.Offset, count * 2);

            var bytes = await plc.ReadBytesAsync(
                ToS7DataType(address.Area), address.DbNo, byteOffset, byteLength, ct)
                .ConfigureAwait(false);

            var words = new ushort[count];
            for (int i = 0; i < count; i++)
                words[i] = (ushort)((bytes[2 * i] << 8) | bytes[2 * i + 1]);
            return CommResult<ushort[]>.Ok(words);
        }
        catch (PlcException ex)
        {
            return CommResult<ushort[]>.Fail(MapError(ex.ErrorCode), ex.ErrorCode.ToString(), ex.Message);
        }
        catch (S7.Net.InvalidDataException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    protected override async Task<CommResult> DoWriteWordsAsync(DeviceAddress address, IReadOnlyList<ushort> values, CancellationToken ct)
    {
        var plc = RequirePlc();
        try
        {
            var bytes = new byte[values.Count * 2];
            for (int i = 0; i < values.Count; i++)
            {
                bytes[2 * i] = (byte)(values[i] >> 8);
                bytes[2 * i + 1] = (byte)values[i];
            }

            var byteOffset = address.Area is "T" or "C" ? address.Offset * 2 : address.Offset;
            await plc.WriteBytesAsync(ToS7DataType(address.Area), address.DbNo, byteOffset, bytes, ct)
                .ConfigureAwait(false);
            return CommResult.Ok();
        }
        catch (PlcException ex)
        {
            return CommResult.Fail(MapError(ex.ErrorCode), ex.ErrorCode.ToString(), ex.Message);
        }
        catch (S7.Net.InvalidDataException ex)
        {
            return CommResult.Fail(CommErrorKind.ProtocolError, null, ex.Message);
        }
    }

    /// <summary>S7-optimized group read: numeric scalars and bits go through one ReadMultipleVars request;
    /// strings and arrays fall back to sequential reads.</summary>
    public override async Task<GroupReadResult> ReadGroupAsync(IReadOnlyList<DevicePoint> points, CancellationToken ct = default)
    {
        var plc = RequirePlc();
        var values = new Dictionary<string, object?>(points.Count, StringComparer.Ordinal);
        var statuses = new Dictionary<string, CommResult>(points.Count, StringComparer.Ordinal);
        var fallback = new List<DevicePoint>();

        var items = new List<DataItem>();
        var itemOwners = new List<(string Name, DevicePoint Point)>();

        foreach (var point in points)
        {
            var varType = TryMapVarType(point.ValueType);
            DeviceAddress address;
            try
            {
                address = ParseAddress(point.Address);
            }
            catch (FormatException ex)
            {
                statuses[point.Name] = CommResult.Fail(CommErrorKind.InvalidAddress, null, ex.Message);
                values[point.Name] = null;
                continue;
            }

            if (varType is null || point.ArrayLength != 1)
            {
                fallback.Add(point);
                continue;
            }

            items.Add(new DataItem
            {
                DataType = ToS7DataType(address.Area),
                DB = address.DbNo,
                StartByteAdr = address.Offset,
                BitAdr = (byte)address.Bit,
                VarType = varType.Value,
                Count = 1,
            });
            itemOwners.Add((point.Name, point));
        }

        if (items.Count > 0)
        {
            var groupStatus = await ExecuteAsync(async token =>
            {
                await plc.ReadMultipleVarsAsync(items, token).ConfigureAwait(false);
                return CommResult<object?>.Ok(null);
            }, ct).ConfigureAwait(false);

            if (groupStatus.Success)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    statuses[itemOwners[i].Name] = CommResult.Ok();
                    values[itemOwners[i].Name] = items[i].Value;
                }
            }
            else
            {
                for (int i = 0; i < items.Count; i++)
                {
                    statuses[itemOwners[i].Name] = CommResult.Fail(groupStatus.Kind, groupStatus.ErrorCode, groupStatus.Message);
                    values[itemOwners[i].Name] = null;
                }
            }
        }

        foreach (var point in fallback)
        {
            var (status, value) = await ReadPointSequentialAsync(point, ct).ConfigureAwait(false);
            statuses[point.Name] = status;
            values[point.Name] = value;
        }

        return new GroupReadResult(values, statuses);
    }

    private async Task<(CommResult Status, object? Value)> ReadPointSequentialAsync(DevicePoint point, CancellationToken ct)
    {
        if (point.ValueType == PlcValueType.String)
        {
            var str = await ReadStringAsync(point.Address, Math.Max((ushort)1, point.ArrayLength), ct).ConfigureAwait(false);
            return (str.WithoutValue(), str.Success ? str.Value : null);
        }

        return point.ValueType switch
        {
            PlcValueType.Bit => await WrapTyped<bool>(point, ct).ConfigureAwait(false),
            PlcValueType.Int16 => await WrapTyped<short>(point, ct).ConfigureAwait(false),
            PlcValueType.UInt16 => await WrapTyped<ushort>(point, ct).ConfigureAwait(false),
            PlcValueType.Int32 => await WrapTyped<int>(point, ct).ConfigureAwait(false),
            PlcValueType.UInt32 => await WrapTyped<uint>(point, ct).ConfigureAwait(false),
            PlcValueType.Int64 => await WrapTyped<long>(point, ct).ConfigureAwait(false),
            PlcValueType.Float32 => await WrapTyped<float>(point, ct).ConfigureAwait(false),
            PlcValueType.Float64 => await WrapTyped<double>(point, ct).ConfigureAwait(false),
            _ => (CommResult.Fail(CommErrorKind.InvalidArgument, null,
                $"Value type {point.ValueType} is not supported by the S7 driver."), null),
        };

        async Task<(CommResult, object?)> WrapTyped<T>(DevicePoint p, CancellationToken token) where T : struct
        {
            var single = p.ArrayLength <= 1;
            if (single)
            {
                var r = await ReadAsync<T>(p.Address, token).ConfigureAwait(false);
                return (r.WithoutValue(), r.Success ? r.Value : null);
            }

            var arr = await ReadWordsAsync(p.Address,
                checked((ushort)(ValueTypeMap.WordCount(p.ValueType) * Math.Max(1, (int)p.ArrayLength))), token)
                .ConfigureAwait(false);
            if (!arr.Success)
                return (arr.WithoutValue(), null);

            int per = ValueTypeMap.WordCount(p.ValueType);
            int length = Math.Max(1, (int)p.ArrayLength);
            var array = Array.CreateInstance(ValueTypeMap.ClrType(p.ValueType), length);
            for (int i = 0; i < length; i++)
                array.SetValue(ValueCodec.DecodeObject(p.ValueType, arr.Value.AsSpan(i * per, per), DataLayout), i);
            return (arr.WithoutValue(), array);
        }
    }

    private static VarType? TryMapVarType(PlcValueType type) => type switch
    {
        PlcValueType.Bit => VarType.Bit,
        PlcValueType.Int16 => VarType.Int,
        PlcValueType.UInt16 => VarType.Word,
        PlcValueType.Int32 => VarType.DInt,
        PlcValueType.UInt32 => VarType.DWord,
        PlcValueType.Float32 => VarType.Real,
        PlcValueType.Float64 => VarType.LReal,
        _ => null,
    };

    private static S7.Net.DataType ToS7DataType(string area) => area switch
    {
        "DB" => S7.Net.DataType.DataBlock,
        "M" => S7.Net.DataType.Memory,
        "I" => S7.Net.DataType.Input,
        "Q" => S7.Net.DataType.Output,
        "T" => S7.Net.DataType.Timer,
        "C" => S7.Net.DataType.Counter,
        _ => throw new FormatException($"Unknown S7 area '{area}'."),
    };

    private static CommErrorKind MapError(ErrorCode code) => code switch
    {
        ErrorCode.ConnectionError or ErrorCode.IPAddressNotAvailable or ErrorCode.SendData => CommErrorKind.ConnectionLost,
        ErrorCode.WrongVarFormat => CommErrorKind.InvalidAddress,
        ErrorCode.WrongCPU_Type => CommErrorKind.InvalidArgument,
        ErrorCode.ReadData or ErrorCode.WriteData => CommErrorKind.DeviceRejected,
        _ => CommErrorKind.ProtocolError,
    };

    private Plc RequirePlc() => _plc ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");
}
