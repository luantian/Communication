using System.IO.Ports;
using System.Net;
using IndustrialCommunication.Configuration;

namespace IndustrialCommunication.Modbus;

public class ModbusTcpOptions : ConnectionOptions
{
    public string Ip { get; set; } = string.Empty;
    public int Port { get; set; } = 502;
    public byte UnitId { get; set; } = 1;

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Ip))
            errors.Add("ip is required.");
        else if (!IPAddress.TryParse(Ip, out _))
            errors.Add($"ip '{Ip}' is not a valid IP address.");
        if (Port is < 1 or > 65535)
            errors.Add($"port {Port} is out of range (1..65535).");
        return errors;
    }
}

public class ModbusRtuOptions : ConnectionOptions
{
    public string PortName { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public byte UnitId { get; set; } = 1;

    /// <summary>Idle time inserted before every request (milliseconds); useful for slow RS485 buses.</summary>
    public int RequestGapMs { get; set; }

    public override IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(PortName))
            errors.Add("portName is required (e.g. \"COM3\" or \"/dev/ttyUSB0\").");
        if (BaudRate is < 110 or > 921600)
            errors.Add($"baudRate {BaudRate} is out of range.");
        if (DataBits is not (5 or 6 or 7 or 8))
            errors.Add($"dataBits {DataBits} must be 5..8.");
        return errors;
    }
}

/// <summary>Modbus UDP options — same shape as TCP.</summary>
public sealed class ModbusUdpOptions : ModbusTcpOptions { }

/// <summary>Modbus ASCII (serial) options — same shape as RTU.</summary>
public sealed class ModbusAsciiOptions : ModbusRtuOptions { }
