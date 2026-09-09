using System.Globalization;

namespace IndustrialCommunication.Keyence;

/// <summary>
/// Keyence upper-link address parsing: word devices DM/EM/FM/ZF/W/TM/Z/CM/VM; bit devices
/// R/MR/LR/CR/B/VB. W/B/VB use hexadecimal numbers (Keyence convention); relay devices are
/// decimal channel+bit (R100 = channel 1, bit 0).
/// </summary>
internal static class KeyenceAddress
{
    internal readonly record struct DeviceInfo(bool IsWord, bool IsHex);

    private static readonly Dictionary<string, DeviceInfo> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DM"] = new(IsWord: true, IsHex: false),
        ["EM"] = new(IsWord: true, IsHex: false),
        ["FM"] = new(IsWord: true, IsHex: false),
        ["ZF"] = new(IsWord: true, IsHex: false),
        ["W"] = new(IsWord: true, IsHex: true),
        ["TM"] = new(IsWord: true, IsHex: false),
        ["Z"] = new(IsWord: true, IsHex: false),
        ["CM"] = new(IsWord: true, IsHex: false),
        ["VM"] = new(IsWord: true, IsHex: false),
        ["R"] = new(IsWord: false, IsHex: false),
        ["MR"] = new(IsWord: false, IsHex: false),
        ["LR"] = new(IsWord: false, IsHex: false),
        ["CR"] = new(IsWord: false, IsHex: false),
        ["B"] = new(IsWord: false, IsHex: true),
        ["VB"] = new(IsWord: false, IsHex: true),
    };

    public static DeviceAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("Keyence address must not be empty.");

        var text = address.Trim();
        int split = 0;
        while (split < text.Length && char.IsLetter(text[split]))
            split++;

        var prefix = split == 0 || split == text.Length ? string.Empty : text[..split].ToUpperInvariant();
        if (prefix.Length == 0 || !Devices.TryGetValue(prefix, out var device))
            throw new FormatException(
                $"'{address}' has an unknown device prefix. Supported: DM, EM, FM, ZF, W, TM, Z, CM, VM (words); R, MR, LR, CR, B, VB (bits).");

        var style = device.IsHex ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (!int.TryParse(text[split..], style, CultureInfo.InvariantCulture, out var number) || number < 0)
            throw new FormatException(
                $"'{text[split..]}' in '{address}' is not a valid {(device.IsHex ? "hexadecimal " : "")}device number.");
        if (number > 999_999)
            throw new FormatException($"'{address}': device number is out of range.");

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

        throw new FormatException($"Unknown Keyence device area '{area}'.");
    }
}
