using System.Globalization;

namespace IndustrialCommunication.Omron;

/// <summary>
/// FINS address parsing: word areas <c>CIO100</c>, <c>W100</c>, <c>H50</c>, <c>DM100</c> (alias <c>D100</c>);
/// bit access via <c>CIO100.5</c>, <c>DM100.3</c> (bit 0..15 within the word).
/// </summary>
internal static class FinsAddress
{
    internal readonly record struct AreaInfo(ushort WordCode, ushort BitCode);

    private static readonly Dictionary<string, AreaInfo> Areas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CIO"] = new(0xB0, 0x30),
        ["W"] = new(0xB1, 0x31),
        ["H"] = new(0xB2, 0x32),
        ["DM"] = new(0x82, 0x02),
        ["D"] = new(0x82, 0x02), // alias for DM
    };

    public static DeviceAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("FINS address must not be empty.");

        var text = address.Trim();
        int split = 0;
        while (split < text.Length && char.IsLetter(text[split]))
            split++;

        var prefix = split == 0 || split == text.Length ? string.Empty : text[..split].ToUpperInvariant();
        if (prefix.Length == 0 || !Areas.TryGetValue(prefix, out var area))
            throw new FormatException(
                $"'{address}' has an unknown FINS area. Supported: CIO, W, H, DM (alias D).");

        var remainder = text[split..];
        var dot = remainder.Split('.');
        if (dot.Length > 2)
            throw new FormatException($"'{address}' is invalid; expected e.g. DM100 or CIO100.5.");

        var word = ParseWord(dot[0], address);
        if (word > 0xFFFF)
            throw new FormatException($"'{address}': word number exceeds the 2-byte address range.");

        if (dot.Length == 1)
            return new DeviceAddress { Area = prefix == "D" ? "DM" : prefix, Offset = word, IsBit = false };

        var bit = dot[1];
        if (!ushort.TryParse(bit, NumberStyles.None, CultureInfo.InvariantCulture, out var bitNo) || bitNo > 15)
            throw new FormatException($"'{address}': bit number must be 0..15.");
        return new DeviceAddress { Area = prefix == "D" ? "DM" : prefix, Offset = word, Bit = bitNo, IsBit = true };
    }

    public static AreaInfo GetArea(string area)
    {
        if (Areas.TryGetValue(area, out var info))
            return info;

        throw new FormatException($"Unknown FINS area '{area}'.");
    }

    private static int ParseWord(string text, string original)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var word) || word < 0)
            throw new FormatException($"'{text}' in '{original}' is not a valid word number.");
        return word;
    }
}
