using System.Globalization;

namespace IndustrialCommunication.LsFEnet;

/// <summary>
/// FEnet variable parsing. Word form "D100"/"M10" → "%DW100"/"%MW10" (device letter + W + decimal
/// word number); bit form "M100.3" → "%MX1603" (absolute bit = word×16 + bit), or the raw absolute
/// form "MX1603" directly. Devices: P M K F T C L N D R ZR U Z.
/// </summary>
internal static class LsFEnetAddress
{
    internal readonly record struct VariableInfo(string WireName, bool IsBit);

    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "P", "M", "K", "F", "T", "C", "L", "N", "D", "R", "ZR", "U", "Z",
    };

    public static DeviceAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("FEnet address must not be empty.");

        var (variable, isBit) = ParseVariable(address.Trim());
        return new DeviceAddress { Area = isBit ? "X" : "W", Offset = 0, Raw = variable, IsBit = isBit };
    }

    /// <summary>Returns the wire variable name (%MW0 style) and whether it addresses bits.</summary>
    public static (string Variable, bool IsBit) ParseVariable(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("FEnet address must not be empty.");

        var text = address.Trim().TrimStart('%');

        // Raw absolute bit form: MX1603
        if (text.Length > 2 && text[..2].Equals("MX", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var absoluteBit)
            && absoluteBit >= 0)
        {
            return ($"%MX{absoluteBit}", true);
        }

        var (device, size) = SplitDevice(text, out var rest);
        if (device.Length == 0 || !Devices.Contains(device))
            throw new FormatException(
                $"'{address}' has an unknown device. Supported: P, M, K, F, T, C, L, N, D, R, ZR, U, Z.");

        // Bit form: M100.3 → absolute bit number
        var dot = rest.Split('.');
        if (dot.Length == 2)
        {
            if (!int.TryParse(dot[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var word) || word < 0)
                throw new FormatException($"'{dot[0]}' in '{address}' is not a valid word number.");
            if (!int.TryParse(dot[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bit) || bit is < 0 or > 15)
                throw new FormatException($"'{dot[1]}' in '{address}' is not a valid bit number (0..15).");
            return ($"%MX{checked(word * 16 + bit)}", true);
        }

        if (dot.Length != 1)
            throw new FormatException($"'{address}' is invalid; use e.g. D100 or M100.3.");

        if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
            throw new FormatException($"'{rest}' in '{address}' is not a valid device number.");

        return size switch
        {
            'W' => ($"%{device}W{number}", false),
            'X' => ($"%{device}X{number}", true),
            _ => throw new FormatException(
                $"'{address}': byte/dword/lword sizes are not supported; use the word form (e.g. {device}100)."),
        };
    }

    private static (string Device, char Size) SplitDevice(string text, out string rest)
    {
        rest = string.Empty;
        if (text.Length == 0)
            return (string.Empty, 'W');

        // Longest-match: ZR before Z
        if (text.Length >= 3 && text.StartsWith("ZR", StringComparison.OrdinalIgnoreCase) && char.IsDigit(text[2]))
        {
            rest = text[2..];
            return ("ZR", 'W');
        }

        int split = 0;
        while (split < text.Length && char.IsLetter(text[split]))
            split++;

        if (split == 0 || split == text.Length)
            return (string.Empty, 'W');

        var letters = text[..split].ToUpperInvariant();
        rest = text[split..];

        // single device letter, optional size letter
        if (letters.Length == 1)
            return (letters, 'W');

        if (letters.Length == 2 && Devices.Contains(letters[..1]))
        {
            var size = letters[1];
            if (size is 'W' or 'X' or 'B' or 'D' or 'L')
                return (letters[..1], size);
        }

        return (string.Empty, 'W');
    }
}
