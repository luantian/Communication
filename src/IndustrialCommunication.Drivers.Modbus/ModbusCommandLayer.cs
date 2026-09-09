using System.Globalization;
using NModbus;

namespace IndustrialCommunication.Modbus;

/// <summary>Maps unified primitives onto IModbusMaster function codes; shared by the TCP and RTU transports.</summary>
internal sealed class ModbusCommandLayer
{
    private const int MaxReadRegisters = 125;
    private const int MaxWriteRegisters = 123;
    private const int MaxReadBits = 2000;
    private const int MaxWriteBits = 1968;

    private readonly IModbusMaster _master;
    private readonly byte _unitId;
    private readonly int _gapMs;

    public ModbusCommandLayer(IModbusMaster master, byte unitId, int gapMs = 0)
    {
        _master = master;
        _unitId = unitId;
        _gapMs = gapMs;
    }

    public async Task<CommResult<ushort[]>> ReadWordsAsync(ModbusArea area, ushort start, ushort count, CancellationToken ct)
    {
        if (ModbusAreaMap.IsBitArea(area))
            return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null, $"Area {area} is a bit area; use bit operations.");
        if (count > MaxReadRegisters)
            return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null, $"Reading {count} registers exceeds the Modbus limit of {MaxReadRegisters}.");

        try
        {
            await DelayGapAsync(ct).ConfigureAwait(false);
            var words = area == ModbusArea.HoldingRegister
                ? await _master.ReadHoldingRegistersAsync(_unitId, start, count).ConfigureAwait(false)
                : await _master.ReadInputRegistersAsync(_unitId, start, count).ConfigureAwait(false);
            return CommResult<ushort[]>.Ok(words);
        }
        catch (SlaveException ex)
        {
            return CommResult<ushort[]>.Fail(CommErrorKind.DeviceRejected,
                ex.SlaveExceptionCode.ToString(CultureInfo.InvariantCulture),
                $"Modbus slave rejected the read: {ex.Message}");
        }
    }

    public async Task<CommResult> WriteWordsAsync(ModbusArea area, ushort start, IReadOnlyList<ushort> values, CancellationToken ct)
    {
        if (area != ModbusArea.HoldingRegister)
            return CommResult.Fail(CommErrorKind.InvalidArgument, null, $"Area {area} is read-only; only HR can be written.");
        if (values.Count > MaxWriteRegisters)
            return CommResult.Fail(CommErrorKind.InvalidArgument, null, $"Writing {values.Count} registers exceeds the Modbus limit of {MaxWriteRegisters}.");

        try
        {
            await DelayGapAsync(ct).ConfigureAwait(false);
            await _master.WriteMultipleRegistersAsync(_unitId, start, [.. values]).ConfigureAwait(false);
            return CommResult.Ok();
        }
        catch (SlaveException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected,
                ex.SlaveExceptionCode.ToString(CultureInfo.InvariantCulture),
                $"Modbus slave rejected the write: {ex.Message}");
        }
    }

    public async Task<CommResult<bool[]>> ReadBitsAsync(ModbusArea area, ushort start, ushort count, CancellationToken ct)
    {
        if (!ModbusAreaMap.IsBitArea(area))
            return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null, $"Area {area} is a word area; use word operations.");
        if (count > MaxReadBits)
            return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null, $"Reading {count} bits exceeds the Modbus limit of {MaxReadBits}.");

        try
        {
            await DelayGapAsync(ct).ConfigureAwait(false);
            var bits = area == ModbusArea.Coil
                ? await _master.ReadCoilsAsync(_unitId, start, count).ConfigureAwait(false)
                : await _master.ReadInputsAsync(_unitId, start, count).ConfigureAwait(false);
            return CommResult<bool[]>.Ok(bits);
        }
        catch (SlaveException ex)
        {
            return CommResult<bool[]>.Fail(CommErrorKind.DeviceRejected,
                ex.SlaveExceptionCode.ToString(CultureInfo.InvariantCulture),
                $"Modbus slave rejected the read: {ex.Message}");
        }
    }

    public async Task<CommResult> WriteBitsAsync(ModbusArea area, ushort start, IReadOnlyList<bool> values, CancellationToken ct)
    {
        if (area != ModbusArea.Coil)
            return CommResult.Fail(CommErrorKind.InvalidArgument, null, $"Area {area} is read-only; only C (coils) can be written.");
        if (values.Count > MaxWriteBits)
            return CommResult.Fail(CommErrorKind.InvalidArgument, null, $"Writing {values.Count} bits exceeds the Modbus limit of {MaxWriteBits}.");

        try
        {
            await DelayGapAsync(ct).ConfigureAwait(false);
            await _master.WriteMultipleCoilsAsync(_unitId, start, [.. values]).ConfigureAwait(false);
            return CommResult.Ok();
        }
        catch (SlaveException ex)
        {
            return CommResult.Fail(CommErrorKind.DeviceRejected,
                ex.SlaveExceptionCode.ToString(CultureInfo.InvariantCulture),
                $"Modbus slave rejected the write: {ex.Message}");
        }
    }

    private async Task DelayGapAsync(CancellationToken ct)
    {
        if (_gapMs > 0)
            await Task.Delay(_gapMs, ct).ConfigureAwait(false);
    }
}
