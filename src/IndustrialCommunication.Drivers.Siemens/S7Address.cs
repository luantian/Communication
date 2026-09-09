using System.Globalization;

namespace IndustrialCommunication.Siemens;

/// <summary>
/// S7 address parsing:
/// <list type="bullet">
/// <item>DB: <c>DB1.DBX10.0</c> (bit), <c>DB1.DBB10</c>, <c>DB1.DBW10</c>, <c>DB1.DBD10</c></item>
/// <item>Merker / Input / Output (aliases I=E, Q=A): <c>M100.5</c> (bit), <c>MW10</c>, <c>MD10</c>, <c>I0.1</c>, <c>QW20</c> ...</item>
/// </list>
/// The size suffix only documents the intended width; the unified model stores the start byte and bit.
/// </summary>
internal static class S7Address
{
    public static DeviceAddress Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("S7 address must not be empty.");

        var text = address.Trim().ToUpperInvariant();
        return text.StartsWith("DB", StringComparison.Ordinal)
            ? ParseDb(text, address)
            : ParseNonDb(text, address);
    }

    private static DeviceAddress ParseDb(string text, string original)
    {
        // DBn.DBX10.0 / DBn.DBB10 / DBn.DBW10 / DBn.DBD10
        var parts = text.Split('.');
        if (parts.Length is not (2 or 3)
            || !parts[0].StartsWith("DB", StringComparison.Ordinal)
            || parts[0].Length <= 2
            || (parts.Length == 3 && !parts[1].StartsWith("DBX", StringComparison.Ordinal)))
        {
            throw new FormatException(
                $"'{original}' is invalid; expected DBn.DBX10.0, DBn.DBB10, DBn.DBW10 or DBn.DBD10.");
        }

        var dbNo = ParseNumber(parts[0][2..], original, "DB number");
        if (dbNo > 0xFFFF)
            throw new FormatException($"'{original}': DB number {dbNo} is out of range.");

        if (parts.Length == 3)
        {
            var byteOffset = ParseNumber(parts[1][3..], original, "byte offset");
            var bit = ParseNumber(parts[2], original, "bit number");
            if (bit > 7)
                throw new FormatException($"'{original}': bit number {bit} is out of range (0..7).");
            return new DeviceAddress { Area = "DB", DbNo = dbNo, Offset = byteOffset, Bit = bit, IsBit = true };
        }

        var tail = parts[1];
        if (tail is not (['D', 'B', 'B', ..] or ['D', 'B', 'W', ..] or ['D', 'B', 'D', ..]))
            throw new FormatException($"'{original}': unknown DB access '{tail}' (use DBX, DBB, DBW or DBD).");

        var offset = ParseNumber(tail[3..], original, "byte offset");
        return new DeviceAddress { Area = "DB", DbNo = dbNo, Offset = offset };
    }

    private static DeviceAddress ParseNonDb(string text, string original)
    {
        // Timer/Counter areas: T5, C10 — one 2-byte S5Time/BCD cell per point (word operations only).
        if (text.Length >= 2 && (text[0] == 'T' || text[0] == 'C') && char.IsDigit(text[1]))
        {
            var number = ParseNumber(text[1..], original, "timer/counter number");
            return new DeviceAddress { Area = text[0] == 'T' ? "T" : "C", Offset = number };
        }

        var area = text[0] switch
        {
            'M' => "M",
            'I' or 'E' => "I",
            'Q' or 'A' => "Q",
            _ => throw new FormatException(
                $"'{original}' is not a valid S7 address. Use DB1.DBW10, DB1.DBX10.0, M100.5, MW10, I0.1, QW20, T5 or C10."),
        };

        var rest = text[1..];
        if (rest.Length > 1 && rest[0] is 'B' or 'W' or 'D')
        {
            var offset = ParseNumber(rest[1..], original, "byte offset");
            return new DeviceAddress { Area = area, Offset = offset };
        }

        // Bit form: with an explicit ".bit".
        var dot = rest.Split('.');
        if (dot.Length == 2)
        {
            var byteOffset = ParseNumber(dot[0], original, "byte offset");
            var bit = ParseNumber(dot[1], original, "bit number");
            if (bit > 7)
                throw new FormatException($"'{original}': bit number {bit} is out of range (0..7).");
            return new DeviceAddress { Area = area, Offset = byteOffset, Bit = bit, IsBit = true };
        }

        throw new FormatException(
            $"'{original}' is ambiguous; use M100.5 for a bit or MW100/MD100 for multi-byte access.");
    }

    private static int ParseNumber(string text, string original, string what)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0)
            return value;

        throw new FormatException($"'{original}': '{text}' is not a valid {what}.");
    }
}
