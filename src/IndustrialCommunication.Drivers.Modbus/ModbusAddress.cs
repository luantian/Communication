using System.Globalization;

namespace IndustrialCommunication.Modbus;

/// <summary>
/// Modbus address parsing. Two forms:
/// <list type="bullet">
/// <item>Named areas with 0-based protocol addresses: <c>HR100</c>, <c>IR0</c>, <c>C10</c>, <c>DR3</c>.</item>
/// <item>Classic 5/6-digit human-readable addresses (1-based, auto-decremented):
/// <c>40001</c>→HR0, <c>30001</c>→IR0, <c>00001</c>→C0, <c>10001</c>→DR0, <c>400001</c>→HR0.</item>
/// </list>
/// </summary>
internal static class ModbusAddress
{
    public static DeviceAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("Modbus address must not be empty.");

        var text = address.Trim();

        var named = ParseNamed(text);
        if (named is not null)
            return named;

        var classic = ParseClassic(text);
        if (classic is not null)
            return classic;

        throw new FormatException(
            $"'{address}' is not a valid Modbus address. Use named areas (HR100, IR0, C10, DR3) or classic 5/6-digit form (40001, 30001, 00001, 10001).");
    }

    private static DeviceAddress? ParseNamed(string text)
    {
        int split = 0;
        while (split < text.Length && char.IsLetter(text[split]))
            split++;

        if (split == 0 || split == text.Length)
            return null;

        var prefix = text[..split].ToUpperInvariant();
        var area = prefix switch
        {
            "HR" or "HOLDING" => ModbusArea.HoldingRegister,
            "IR" or "INPUTREG" => ModbusArea.InputRegister,
            "C" or "COIL" => ModbusArea.Coil,
            "DR" or "DI" => ModbusArea.DiscreteInput,
            _ => throw new FormatException($"Unknown Modbus area prefix '{prefix}' (use HR, IR, C or DR)."),
        };

        var number = text[split..];
        if (!ushort.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            throw new FormatException($"'{number}' in '{text}' is not a valid 0-based address (0..65535).");

        return new DeviceAddress
        {
            Area = ModbusAreaMap.ToAreaName(area),
            Offset = offset,
            IsBit = ModbusAreaMap.IsBitArea(area),
        };
    }

    private static DeviceAddress? ParseClassic(string text)
    {
        // Classic form is pure digits; HR100-style already handled above.
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return null;

        return text.Length switch
        {
            5 => ParseClassic5(value, text),
            6 => ParseClassic6(value, text),
            _ => throw new FormatException(
                $"'{text}' is not a valid classic Modbus address (expected 5 or 6 digits like 40001 or 400001)."),
        };
    }

    private static DeviceAddress ParseClassic5(uint value, string text)
    {
        var area = (text[0] - '0') switch
        {
            0 => ModbusArea.Coil,
            1 => ModbusArea.DiscreteInput,
            3 => ModbusArea.InputRegister,
            4 => ModbusArea.HoldingRegister,
            _ => throw new FormatException(
                $"'{text}' has an unknown area digit '{text[0]}' (classic form starts with 0, 1, 3 or 4)."),
        };

        int baseOffset = area switch
        {
            ModbusArea.Coil => 0,
            ModbusArea.DiscreteInput => 10000,
            ModbusArea.InputRegister => 30000,
            _ => 40000,
        };

        int offset = (int)value - baseOffset - 1;
        if (offset < 0 || offset > ushort.MaxValue)
            throw new FormatException($"'{text}' is out of range for its area (first usable address is {baseOffset + 1}).");

        return new DeviceAddress
        {
            Area = ModbusAreaMap.ToAreaName(area),
            Offset = offset,
            IsBit = ModbusAreaMap.IsBitArea(area),
        };
    }

    private static DeviceAddress ParseClassic6(uint value, string text)
    {
        var area = (text[0] - '0') switch
        {
            0 => ModbusArea.Coil,
            1 => ModbusArea.DiscreteInput,
            3 => ModbusArea.InputRegister,
            4 => ModbusArea.HoldingRegister,
            _ => throw new FormatException(
                $"'{text}' has an unknown area digit '{text[0]}' (classic form starts with 0, 1, 3 or 4)."),
        };

        int baseOffset = area switch
        {
            ModbusArea.Coil => 0,
            ModbusArea.DiscreteInput => 100000,
            ModbusArea.InputRegister => 300000,
            _ => 400000,
        };

        int offset = (int)value - baseOffset - 1;
        if (offset < 0 || offset > ushort.MaxValue)
            throw new FormatException($"'{text}' is out of range for its area (offset must be 0..65535).");

        return new DeviceAddress
        {
            Area = ModbusAreaMap.ToAreaName(area),
            Offset = offset,
            IsBit = ModbusAreaMap.IsBitArea(area),
        };
    }
}
