namespace IndustrialCommunication.Modbus;

internal enum ModbusArea
{
    /// <summary>Holding register (4x). Read: fc03, write: fc06/fc16.</summary>
    HoldingRegister,

    /// <summary>Input register (3x). Read-only: fc04.</summary>
    InputRegister,

    /// <summary>Coil (0x). Read: fc01, write: fc05/fc15.</summary>
    Coil,

    /// <summary>Discrete input (1x). Read-only: fc02.</summary>
    DiscreteInput,
}

internal static class ModbusAreaMap
{
    /// <summary>Canonical area names used in DeviceAddress.Area.</summary>
    public const string HoldingRegister = "HR";
    public const string InputRegister = "IR";
    public const string Coil = "C";
    public const string DiscreteInput = "DR";

    public static string ToAreaName(ModbusArea area) => area switch
    {
        ModbusArea.HoldingRegister => HoldingRegister,
        ModbusArea.InputRegister => InputRegister,
        ModbusArea.Coil => Coil,
        ModbusArea.DiscreteInput => DiscreteInput,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };

    public static ModbusArea FromAreaName(string areaName) => areaName switch
    {
        HoldingRegister => ModbusArea.HoldingRegister,
        InputRegister => ModbusArea.InputRegister,
        Coil => ModbusArea.Coil,
        DiscreteInput => ModbusArea.DiscreteInput,
        _ => throw new FormatException($"Unknown Modbus area '{areaName}' (use HR, IR, C or DR)."),
    };

    public static bool IsBitArea(ModbusArea area) =>
        area is ModbusArea.Coil or ModbusArea.DiscreteInput;

    public static bool IsWritable(ModbusArea area) =>
        area is ModbusArea.HoldingRegister or ModbusArea.Coil;
}
