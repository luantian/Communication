using System.Globalization;

namespace IndustrialCommunication.GeSrtp;

/// <summary>
/// GE SRTP address parsing: word memories %R/%AI/%AQ (R100, AI10, AQ10 — with or without the % sign);
/// bit memories %I/%Q/%M/%T/%SA/%SB/%SC/%S/%G (I10, Q20, M100 ...). The % prefix is optional.
/// </summary>
internal static class GeSrtpAddress
{
    internal readonly record struct MemoryInfo(byte Selector, bool IsWord);

    private static readonly Dictionary<string, MemoryInfo> Memories = new(StringComparer.OrdinalIgnoreCase)
    {
        // Word memories (ReadWords/WriteWords primitives)
        ["R"] = new(0x08, IsWord: true),
        ["AI"] = new(0x0A, IsWord: true),
        ["AQ"] = new(0x0C, IsWord: true),
        // Bit memories (ReadBits/WriteBits primitives)
        ["I"] = new(0x46, IsWord: false),
        ["Q"] = new(0x48, IsWord: false),
        ["M"] = new(0x4C, IsWord: false),
        ["T"] = new(0x4A, IsWord: false),
        ["SA"] = new(0x4E, IsWord: false),
        ["SB"] = new(0x50, IsWord: false),
        ["SC"] = new(0x52, IsWord: false),
        ["S"] = new(0x54, IsWord: false),
        ["G"] = new(0x56, IsWord: false),
    };

    public static DeviceAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("GE SRTP address must not be empty.");

        var text = address.Trim().TrimStart('%');
        int split = 0;
        while (split < text.Length && char.IsLetter(text[split]))
            split++;

        var prefix = split == 0 || split == text.Length ? string.Empty : text[..split].ToUpperInvariant();
        if (prefix.Length == 0 || !Memories.TryGetValue(prefix, out var memory))
            throw new FormatException(
                $"'{address}' has an unknown memory type. Supported: R, AI, AQ (words); I, Q, M, T, SA, SB, SC, S, G (bits).");

        if (!int.TryParse(text[split..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || number < 1)
            throw new FormatException($"'{text[split..]}' in '{address}' is not a valid 1-based address.");

        return new DeviceAddress
        {
            Area = prefix,
            Offset = number, // 1-based in GE syntax; frames subtract 1
            IsBit = !memory.IsWord,
        };
    }

    public static MemoryInfo GetMemory(string area)
    {
        if (Memories.TryGetValue(area, out var memory))
            return memory;

        throw new FormatException($"Unknown GE SRTP memory type '{area}'.");
    }
}
