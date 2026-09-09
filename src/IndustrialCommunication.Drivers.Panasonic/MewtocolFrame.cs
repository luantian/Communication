using System.Globalization;
using System.Text;

namespace IndustrialCommunication.Panasonic;

/// <summary>
/// MEWTOCOL-COM frame building and parsing (pure functions). Frames:
///   command   % station(2) # command data BCC(2) CR
///   response  % station(2) $ respCode data BCC(2) CR
///   error     % station(2) ! errorCode(2) BCC(2) CR
/// BCC = XOR of every byte from '%' through the last data character, as two uppercase hex digits.
/// Word data = four hex digits per word with the LOW byte first (0x0063 travels as "6300").
/// Relay numbers = 3-digit decimal word + 1 hex digit bit (R0101 = word 10, bit 1).
/// </summary>
internal static class MewtocolFrame
{
    /// <summary>Standard (%) frames carry at most 118 characters → 27 words per read; longer reads use the extended (&lt;) header with multi-frame continuation.</summary>
    public const int MaxWordsPerRequest = 509;

    /// <summary>Multi-frame continuation request: sent after every intermediate frame (ends with '&amp;'). BCC skipped with **.</summary>
    public static string BuildContinuationRequest(int station) =>
        $"%{station:00}**&\r";

    public static string BuildReadWords(int station, int start, int end) =>
        Frame(station, "RDD", $"{start:00000}{end:00000}");

    public static string BuildWriteWords(int station, int start, int end, IReadOnlyList<ushort> words)
    {
        var data = new StringBuilder();
        foreach (var word in words)
            data.Append(WordsHex(word));
        return Frame(station, "WDD", $"{start:00000}{end:00000}{data}");
    }

    public static string BuildReadContact(int station, char area, int word, int bit) =>
        Frame(station, "RCS", $"{area}{word:000}{bit:X}");

    public static string BuildWriteContact(int station, char area, int word, int bit, bool on) =>
        Frame(station, "WCS", $"{area}{word:000}{bit:X}{(on ? '1' : '0')}");

    public static string BuildStatusProbe(int station) =>
        Frame(station, "RT", string.Empty);

    /// <summary>Splits a response line into (responseCode, data); throws on error replies, bad BCC or malformed frames.
    /// Accepts both '%' and '&lt;' headers (extended frames) and reports intermediate multi-frame
    /// segments (payload ending in '&amp;') via <paramref name="hasMore"/> for continuation handling.</summary>
    public static (string ResponseCode, string Data) ParseResponse(string line, out bool hasMore)
    {
        hasMore = false;
        // "%01$RD6300...62" → code "RD", data "6300..."; minimum "%01$RD" + BCC
        if (line.Length < 8 || (line[0] != '%' && line[0] != '<'))
            throw new InvalidDataException($"Mewtocol response '{line}' is malformed.");

        if (line[3] == '!')
        {
            var errorCode = line[4..6];
            VerifyBcc(line);
            throw new MewtocolErrorException(errorCode);
        }

        if (line[3] != '$')
            throw new InvalidDataException($"Mewtocol response '{line}' has neither '$' nor '!' after the station.");

        VerifyBcc(line);
        var code = line[4..6];
        var payload = line[6..^2];

        // Multi-frame intermediate segments end with '&' (before their BCC) — strip it and signal continuation.
        if (payload.EndsWith('&'))
        {
            hasMore = true;
            payload = payload[..^1];
        }

        return (code, payload);
    }

    /// <summary>Single-frame compatibility overload.</summary>
    public static (string ResponseCode, string Data) ParseResponse(string line) => ParseResponse(line, out _);

    public static ushort[] ParseWords(string data, int count)
    {
        if (data.Length < count * 4)
            throw new InvalidDataException(
                $"Mewtocol response carries {data.Length / 4} word(s) but {count} were requested.");

        var words = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            var chunk = data.Substring(i * 4, 4);
            if (!ushort.TryParse(chunk, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
                throw new InvalidDataException($"'{chunk}' in the Mewtocol response is not a valid hex word.");
            // low byte first
            words[i] = (ushort)(((packed & 0xFF) << 8) | (packed >> 8));
        }
        return words;
    }

    public static string WordsHex(ushort word) =>
        // 0x0063 → "6300"
        ((byte)word).ToString("X2", CultureInfo.InvariantCulture)
        + (word >> 8).ToString("X2", CultureInfo.InvariantCulture);

    private static string Frame(int station, string command, string data)
    {
        var body = $"%{station:00}#{command}{data}";
        var bcc = Bcc(body);
        return body + bcc + "\r";
    }

    private static string Bcc(string body)
    {
        byte acc = 0;
        foreach (var b in Encoding.ASCII.GetBytes(body))
            acc ^= b;
        return acc.ToString("X2", CultureInfo.InvariantCulture);
    }

    private static void VerifyBcc(string line)
    {
        var expected = line[^2..];
        byte acc = 0;
        foreach (var b in Encoding.ASCII.GetBytes(line[..^2]))
            acc ^= b;
        var actual = acc.ToString("X2", CultureInfo.InvariantCulture);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Mewtocol response BCC is '{expected}' but '{actual}' was computed.");
    }
}

/// <summary>Mewtocol error reply "!nn" — the PLC rejected the command.</summary>
internal sealed class MewtocolErrorException : Exception
{
    public string Code { get; }

    public MewtocolErrorException(string code)
        : base($"The PLC answered with Mewtocol error {code} ({Describe(code)}).")
    {
        Code = code;
    }

    private static string Describe(string code) => code switch
    {
        "22" => "receive buffer overflow at the target",
        "24" => "communication unit hardware error",
        "28" => "no response timeout",
        "40" => "BCC mismatch",
        "41" => "frame format error",
        "42" => "command not supported",
        "43" => "multi-frame sequence error",
        "60" => "parameter error (unknown area or function code)",
        "61" => "data error (number out of range or bad format)",
        "63" => "mode error (command disabled in the current mode)",
        "65" => "write protection active",
        "66" => "address error",
        _ => "unknown error",
    };
}
