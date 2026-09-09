using IndustrialCommunication.Modbus;
using Xunit;

namespace IndustrialCommunication.Tests;

public class ModbusOptionsTests
{
    [Fact]
    public void Tcp_options_report_ip_and_port_errors()
    {
        var bad = new ModbusTcpOptions { Ip = "not-an-ip", Port = 70000 };
        Assert.Equal(2, bad.Validate().Count);

        var good = new ModbusTcpOptions { Ip = "192.168.1.20", Port = 502 };
        Assert.Empty(good.Validate());
    }

    [Fact]
    public void Rtu_options_report_missing_port_and_bad_values()
    {
        var bad = new ModbusRtuOptions { PortName = "", BaudRate = 5, DataBits = 9 };
        Assert.Equal(3, bad.Validate().Count);

        var good = new ModbusRtuOptions { PortName = "COM3", BaudRate = 9600 };
        Assert.Empty(good.Validate());
    }

    [Fact]
    public async Task Rtu_client_connect_to_missing_port_reports_connection_lost()
    {
        // No COM port "COM_MISSING" exists on any machine — the connect must fail cleanly.
        var host = IndustrialCommunication.CommunicationHost.FromConfig(
            Configuration.CommunicationConfigLoader.Parse("""
                { "defaults": { "connectRetries": 0, "connectTimeoutMs": 1500 },
                  "devices": [ { "name": "rtu", "protocol": "ModbusRtu",
                                 "connection": { "portName": "COM_MISSING" } } ] }
                """),
            r => r.AddModbusRtu());

        await using (host)
        {
            var client = host.GetClient("rtu");
            var read = await client.ReadWordsAsync("HR0", 1);

            Assert.False(read.Success);
            Assert.Equal(CommErrorKind.ConnectionLost, read.Kind);
        }
    }

    [Fact]
    public async Task Ascii_client_connect_to_missing_port_reports_connection_lost()
    {
        var host = IndustrialCommunication.CommunicationHost.FromConfig(
            Configuration.CommunicationConfigLoader.Parse("""
                { "defaults": { "connectRetries": 0, "connectTimeoutMs": 1500 },
                  "devices": [ { "name": "ascii", "protocol": "ModbusAscii",
                                 "connection": { "portName": "COM_MISSING" } } ] }
                """),
            r => r.AddModbusAscii());

        await using (host)
        {
            var client = host.GetClient("ascii");
            var read = await client.ReadWordsAsync("HR0", 1);

            Assert.False(read.Success);
            Assert.Equal(CommErrorKind.ConnectionLost, read.Kind);
        }
    }
}
