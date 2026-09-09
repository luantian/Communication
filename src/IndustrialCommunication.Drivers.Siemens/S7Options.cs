using System.Net;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Siemens;

public sealed class S7Options : ConnectionOptions
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 102;

    /// <summary>CPU family as string: S7200, S7200Smart, S7300, S7400, S71200, S71500 (case-insensitive).</summary>
    public string CpuType { get; set; } = "S71200";

    public short Rack { get; set; }
    public short Slot { get; set; }

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
            ResolveCpuType();
        }
        catch (FormatException ex)
        {
            errors.Add(ex.Message);
        }
        return errors;
    }

    public S7.Net.CpuType ResolveCpuType() => CpuType.Trim().ToUpperInvariant() switch
    {
        "S7200" => S7.Net.CpuType.S7200,
        "S7200SMART" or "S7200-SMART" => S7.Net.CpuType.S7200Smart,
        "S7300" => S7.Net.CpuType.S7300,
        "S7400" => S7.Net.CpuType.S7400,
        "S71200" => S7.Net.CpuType.S71200,
        "S71500" => S7.Net.CpuType.S71500,
        _ => throw new FormatException(
            $"cpuType '{CpuType}' is unknown. Use S7200, S7200Smart, S7300, S7400, S71200 or S71500."),
    };
}
