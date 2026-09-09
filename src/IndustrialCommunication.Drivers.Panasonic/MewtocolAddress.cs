using System.Globalization;

namespace IndustrialCommunication.Panasonic;

/// <summary>
/// MEWTOCOL address parsing. Word devices: <c>DT100</c> (also FL/LD via <c>FL</c>/<c>LD</c> prefixes).
/// Bit devices X/Y/R/L in native notation <c>R0101</c> (3-digit decimal word + 1 hex bit) or
/// the friendlier <c>R10.1</c> form.
/// </summary>
internal static class MewtocolAddress
{
    internal readonly record struct DeviceInfo(char AreaCode, bool IsWord);

    private static readonly Dictionary<string, DeviceInfo> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        // Word devices (RD/WD commands, 5-digit decimal addresses)
        ["DT"] = new('D', IsWord: true),
        ["D"] = new('D', IsWord: true),
        ["FL"] = new('F', IsWord: true),
        ["F"] = new('F', IsWord: true),
        ["LD"] = new('L', IsWord: true),
        // Bit devices (RCS/WCS commands, word+bit numbering)
        ["X"] = new('X', IsWord: false),
        ["Y"] = new('Y', IsWord: false),
        ["R"] = new('R', IsWord: false),
        ["LR"] = new('L', IsWord: false),
    };

    public static DeviceAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("Mewtocol address must not be empty.");

        var text = address.Trim();
        int split = 0;
        while (split < text.Length && char.IsLetter(text[split]))
            split++;

        var prefix = split == 0 || split == text.Length ? string.Empty : text[..split].ToUpperInvariant();
        if (prefix.Length == 0 || !Devices.TryGetValue(prefix, out var device))
            throw new FormatException(
                $"'{address}' has an unknown device prefix. Supported: DT (alias D), FL (alias F), LD, X, Y, R, LR.");

        var remainder = text[split..];
        if (device.IsWord)
        {
            if (!int.TryParse(remainder, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wordNumber)
                || wordNumber < 0 || wordNumber > 99_999)
                throw new FormatException($"'{remainder}' in '{address}' is not a valid 5-digit word number.");
            return new DeviceAddress { Area = prefix == "D" ? "DT" : prefix, Offset = wordNumber, IsBit = false };
        }

        // Bit forms: "0101" (3-digit decimal word + 1 hex digit) or "10.1" (word.bit)
        int word;
        int bit;
        var dot = remainder.Split('.');
        if (dot.Length == 2)
        {
            if (!int.TryParse(dot[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out word) || word < 0 || word > 999)
                throw new FormatException($"'{dot[0]}' in '{address}' is not a valid word number (0..999).");
            if (!int.TryParse(dot[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bit) || bit < 0 || bit > 15)
                throw new FormatException($"'{dot[1]}' in '{address}' is not a valid hex bit number (0..F).");
        }
        else if (remainder.Length == 4)
        {
            if (!int.TryParse(remainder[..3], NumberStyles.Integer, CultureInfo.InvariantCulture, out word) || word > 999)
                throw new FormatException($"'{remainder[..3]}' in '{address}' is not a valid 3-digit word number.");
            if (!int.TryParse(remainder[3..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bit) || bit > 15)
                throw new FormatException($"'{remainder[3..]}' in '{address}' is not a valid hex bit number (0..F).");
        }
        else
        {
            throw new FormatException(
                $"'{address}' is invalid; use the native form R0101 (word 10, bit 1) or R10.1.");
        }

        return new DeviceAddress { Area = prefix == "LR" ? "LR" : prefix, Offset = word, Bit = bit, IsBit = true };
    }

    public static DeviceInfo GetDevice(string area)
    {
        if (Devices.TryGetValue(area, out var device))
            return device;

        throw new FormatException($"Unknown Mewtocol device area '{area}'.");
    }
}
