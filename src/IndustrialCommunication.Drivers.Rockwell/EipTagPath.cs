
using System.Globalization;
using System.Text;

namespace IndustrialCommunication.Rockwell;

/// <summary>
/// Logix tag → CIP IOI path encoding. "MyTag", "MyArray[7]", "PID1.Setpoint", "MyArray[2,5]" and
/// bit-of-DINT ("MyDint.3") are supported; the trailing bit number is reported separately because
/// Logix reads whole words and the driver extracts the bit locally.
/// </summary>
internal sealed record EipTagPath(byte[] Ioi, int Bit)
{
    public bool HasBit => Bit >= 0;

    public static EipTagPath Parse(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            throw new FormatException("Logix tag must not be empty.");

        var parts = tag.Split('.');
        var ioi = new StringBuilder();

        int bit = -1;
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.Length == 0)
                throw new FormatException($"'{tag}' has an empty path segment.");

            if (i == parts.Length - 1 && !part.Contains('[') && IsBitNumber(part))
            {
                // trailing "MyDint.3" → bit of the previous (word-sized) segment
                bit = int.Parse(part, CultureInfo.InvariantCulture);
                if (bit > 31)
                    throw new FormatException($"'{tag}': bit number {bit} is out of range (0..31).");
                continue;
            }

            var bracket = part.IndexOf('[');
            var name = bracket < 0 ? part : part[..bracket];
            if (name.Length == 0 || !IsValidSymbol(name))
                throw new FormatException($"'{part}' is not a valid Logix symbol name.");

            AppendSymbol(ioi, name);

            if (bracket >= 0)
            {
                if (!part.EndsWith(']'))
                    throw new FormatException($"'{part}' has an unterminated index bracket.");
                var indices = part[(bracket + 1)..^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var indexText in indices)
                {
                    if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
                        throw new FormatException($"'{indexText}' in '{part}' is not a valid array index.");
                    AppendIndex(ioi, index);
                }
            }
        }

        // Empty IOI means the tag was just a bit number — reject.
        var bytes = Convert.FromHexString(ioi.ToString());
        if (bytes.Length == 0)
            throw new FormatException($"'{tag}' does not name a tag.");
        if (bytes.Length % 2 != 0)
            throw new InvalidOperationException("IOI must be even-length."); // append routines pad

        return new EipTagPath(bytes, bit);
    }

    private static bool IsBitNumber(string text) =>
        text.Length <= 2 && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 31;

    private static bool IsValidSymbol(string name) =>
        !char.IsDigit(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>0x91 + length + ASCII, padded to an even byte count.</summary>
    private static void AppendSymbol(StringBuilder ioi, string name)
    {
        ioi.Append("91");
        ioi.Append(name.Length.ToString("X2", CultureInfo.InvariantCulture));
        foreach (var c in name)
            ioi.Append(((byte)c).ToString("X2", CultureInfo.InvariantCulture));
        if ((name.Length & 1) == 1)
            ioi.Append("00");
    }

    private static void AppendIndex(StringBuilder ioi, int index)
    {
        if (index <= 0xFF)
        {
            ioi.Append("28").Append(index.ToString("X2", CultureInfo.InvariantCulture));
        }
        else if (index <= 0xFFFF)
        {
            // 29 + pad byte + 2-byte little-endian value
            ioi.Append("2900")
                .Append((index & 0xFF).ToString("X2", CultureInfo.InvariantCulture))
                .Append(((index >> 8) & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
        }
        else
        {
            // 2A + pad byte + 4-byte little-endian value
            ioi.Append("2A00")
                .Append((index & 0xFF).ToString("X2", CultureInfo.InvariantCulture))
                .Append(((index >> 8) & 0xFF).ToString("X2", CultureInfo.InvariantCulture))
                .Append(((index >> 16) & 0xFF).ToString("X2", CultureInfo.InvariantCulture))
                .Append(((index >> 24) & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
        }
    }
}
