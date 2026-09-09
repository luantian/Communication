using System.Text;

namespace IndustrialCommunication.Configuration;

/// <summary>
/// Base class for driver-specific connection options (the <c>connection</c> node of a device entry).
/// </summary>
public abstract class ConnectionOptions
{
    /// <summary>Byte/word order of multi-word values in this device.</summary>
    public DataLayout DataLayout { get; set; } = DataLayout.ABCD;

    /// <summary>
    /// String encoding name (e.g. "utf-8", "ascii", "gb2312", "shift-jis"); null/empty defaults to UTF-8.
    /// GB2312/Shift-JIS etc. require the CodePagesEncodingProvider to be registered by the application.
    /// </summary>
    public string? Encoding { get; set; }

    /// <summary>Returns validation errors; empty list means valid.</summary>
    public virtual IReadOnlyList<string> Validate() => [];

    public Encoding ResolveEncoding()
    {
        if (string.IsNullOrWhiteSpace(Encoding))
            return System.Text.Encoding.UTF8;
        try
        {
            return System.Text.Encoding.GetEncoding(Encoding.Trim());
        }
        catch (ArgumentException ex)
        {
            throw new CommunicationException(
                CommErrorKind.InvalidArgument, null, $"Unknown encoding '{Encoding}'. {ex.Message}");
        }
    }
}
