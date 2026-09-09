using System.Net;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Mitsubishi;

public enum McFrameKind
{
    Frame3E,
    Frame4E,
}

public sealed class McOptions : ConnectionOptions
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 5007;

    /// <summary>"3E" or "4E" (4E adds a serial number to every request; default 4E).</summary>
    public string Frame { get; set; } = "4E";

    public byte NetworkNo { get; set; }
    public byte PcNo { get; set; } = 0xFF;
    public byte StationNo { get; set; }

    /// <summary>How long the PLC waits for processing to finish before answering with a timeout error.</summary>
    public int MonitorTimerMs { get; set; } = 3000;

    public McOptions()
    {
        // MELSEC transmits little-endian words (low word first), which is word-swapped
        // relative to the big-endian logical byte order — CDAB in the unified model.
        DataLayout = DataLayout.CDAB;
    }

    public McFrameKind ResolveFrame() => Frame.Trim().ToUpperInvariant() switch
    {
        "3E" => McFrameKind.Frame3E,
        "4E" => McFrameKind.Frame4E,
        _ => throw new FormatException($"frame '{Frame}' is unknown; use \"3E\" or \"4E\"."),
    };

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Ip))
            errors.Add("ip is required.");
        else if (!IPAddress.TryParse(Ip, out _))
            errors.Add($"ip '{Ip}' is not a valid IP address.");
        if (Port is < 1 or > 65535)
            errors.Add($"port {Port} is out of range (1..65535).");
        try
        {
            ResolveFrame();
        }
        catch (FormatException ex)
        {
            errors.Add(ex.Message);
        }
        if (MonitorTimerMs < 0)
            errors.Add("monitorTimerMs must not be negative.");
        return errors;
    }
}
