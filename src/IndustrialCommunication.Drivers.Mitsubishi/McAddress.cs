using System.Globalization;

namespace IndustrialCommunication.Mitsubishi;

/// <summary>
/// MC-protocol address parsing: device prefix + number, e.g. <c>D100</c>, <c>W1F</c>, <c>R500</c>,
/// <c>M100</c>, <c>X0A</c>, <c>Y1F</c>, <c>B0</c>. X/Y/B/W/SB/SW use hexadecimal numbers (Mitsubishi convention).
/// </summary>
internal static class McAddress
{
    internal readonly record struct DeviceInfo(byte Code, bool IsWord, bool IsHex);

    private static readonly Dictionary<string, DeviceInfo> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        // SLMP single-byte extended device codes; the key is the canonical area name.
        ["X"] = new(0x9C, IsWord: false, IsHex: true),
        ["Y"] = new(0x9D, IsWord: false, IsHex: true),
        ["M"] = new(0x90, IsWord: false, IsHex: false),
        ["L"] = new(0x92, IsWord: false, IsHex: false),
        ["F"] = new(0x93, IsWord: false, IsHex: false),
        ["V"] = new(0x94, IsWord: false, IsHex: false),
        ["B"] = new(0xA0, IsWord: false, IsHex: true),
        ["S"] = new(0x98, IsWord: false, IsHex: false),
        ["SB"] = new(0xA1, IsWord: false, IsHex: true),
        ["D"] = new(0xA8, IsWord: true, IsHex: false),
        ["W"] = new(0xB4, IsWord: true, IsHex: true),
        ["R"] = new(0xAF, IsWord: true, IsHex: false),
        ["ZR"] = new(0xB0, IsWord: true, IsHex: false),
        ["SM"] = new(0x91, IsWord: false, IsHex: false),
        ["SD"] = new(0xA9, IsWord: true, IsHex: false),
        ["SW"] = new(0xB5, IsWord: true, IsHex: true),
    };

    public static DeviceAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("MC address must not be empty.");

        var text = address.Trim();
        int split = 0;
        while (split < text.Length && char.IsLetter(text[split]))
            split++;

        var prefix = split == 0 || split == text.Length ? string.Empty : text[..split].ToUpperInvariant();
        if (prefix.Length == 0 || !Devices.TryGetValue(prefix, out var device))
            throw new FormatException(
                $"'{address}' has an unknown device prefix. Supported: D, W, R, ZR, M, L, F, V, B, S, X, Y, SM, SD, SB, SW.");

        var numberText = text[split..];
        var style = device.IsHex ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!int.TryParse(numberText, style, CultureInfo.InvariantCulture, out var number) || number < 0)
            throw new FormatException(
                $"'{numberText}' in '{address}' is not a valid {(device.IsHex ? "hexadecimal " : "")}device number.");
        if (number > 0xFF_FFFF)
            throw new FormatException($"'{address}': device number exceeds the 3-byte address range.");

        return new DeviceAddress
        {
            Area = prefix,
            Offset = number,
            IsBit = !device.IsWord,
        };
    }

    public static DeviceInfo GetDevice(string area)
    {
        if (Devices.TryGetValue(area, out var device))
            return device;

        throw new FormatException($"Unknown MC device area '{area}'.");
    }
}
