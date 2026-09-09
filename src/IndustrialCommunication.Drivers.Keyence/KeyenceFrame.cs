using System.Globalization;
using System.Text;

namespace IndustrialCommunication.Keyence;

/// <summary>
/// Keyence upper-link frame building and parsing (pure functions). Plain ASCII protocol:
/// commands end with CR, responses with CR LF; read responses are space-separated values,
/// writes answer "OK", failures "E&lt;n&gt;". Word access requests the .U format
/// (fixed-width unsigned 16-bit decimal); bit access returns 0/1 per point.
/// </summary>
internal static class KeyenceFrame
{
    public const int MaxPointsPerRequest = 256;

    public static string BuildReadWords(string area, int number, int count) =>
        $"RDS {area}{number}.U {count}\r";

    public static string BuildWriteWords(string area, int number, IReadOnlyList<ushort> values)
    {
        var sb = new StringBuilder()
            .Append("WRS ").Append(area).Append(number).Append(".U ").Append(values.Count);
        foreach (var value in values)
            sb.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture));
        return sb.Append('\r').ToString();
    }

    public static string BuildReadBits(string area, int number, int count) =>
        $"RDS {area}{number} {count}\r";

    public static string BuildWriteBits(string area, int number, IReadOnlyList<bool> values)
    {
        var sb = new StringBuilder()
            .Append("WRS ").Append(area).Append(number).Append(' ').Append(values.Count);
        foreach (var value in values)
            sb.Append(' ').Append(value ? '1' : '0');
        return sb.Append('\r').ToString();
    }

    public static string BuildErrorStatusProbe() => "?E\r";

    /// <summary>Splits a response line into tokens; throws <see cref="KeyenceErrorException"/> on "E&lt;n&gt;" replies.</summary>
    public static string[] ParseTokens(string line)
    {
        if (line.Length >= 2 && line[0] == 'E' && char.IsDigit(line[1]))
            throw new KeyenceErrorException(line[..2]);

        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static ushort[] ParseWords(string line, int count)
    {
        var tokens = ParseTokens(line);
        if (tokens.Length < count)
            throw new InvalidDataException(
                $"Keyence response carries {tokens.Length} value(s) but {count} were requested.");

        var values = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            if (!ushort.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                throw new InvalidDataException($"'{tokens[i]}' in the Keyence response is not a valid word value.");
        }
        return values;
    }

    public static bool[] ParseBits(string line, int count)
    {
        var tokens = ParseTokens(line);
        if (tokens.Length < count)
            throw new InvalidDataException(
                $"Keyence response carries {tokens.Length} value(s) but {count} were requested.");

        var values = new bool[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = tokens[i] switch
            {
                "1" => true,
                "0" => false,
                _ => throw new InvalidDataException($"'{tokens[i]}' in the Keyence response is not a bit value."),
            };
        }
        return values;
    }
}

/// <summary>Keyence error reply "E0".."E6" — the PLC rejected the command.</summary>
internal sealed class KeyenceErrorException : Exception
{
    public string Code { get; }

    public KeyenceErrorException(string code)
        : base($"The PLC answered with Keyence error {code} ({Describe(code)}).")
    {
        Code = code;
    }

    private static string Describe(string code) => code switch
    {
        "E0" => "device/unit/bank number out of range",
        "E1" => "command or format error",
        "E2" => "RUN start without a logged-in program",
        "E4" => "write protected",
        "E6" => "no comment data",
        _ => "unknown error",
    };
}
