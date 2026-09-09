using IndustrialCommunication.Configuration;
using IndustrialCommunication.Modbus;

namespace IndustrialCommunication;

public static class ModbusDriverRegistration
{
    /// <summary>Registers the Modbus TCP driver for protocol name "ModbusTcp".</summary>
    public static DriverRegistry AddModbusTcp(this DriverRegistry registry) =>
        registry.Register<ModbusTcpOptions>(
            ProtocolNames.ModbusTcp,
            static (options, device, context) => new ModbusTcpClient(options, device, context));

    /// <summary>Registers the Modbus RTU (serial) driver for protocol name "ModbusRtu".</summary>
    public static DriverRegistry AddModbusRtu(this DriverRegistry registry) =>
        registry.Register<ModbusRtuOptions>(
            ProtocolNames.ModbusRtu,
            static (options, device, context) => new ModbusRtuClient(options, device, context));

    /// <summary>Registers the Modbus UDP driver for protocol name "ModbusUdp".</summary>
    public static DriverRegistry AddModbusUdp(this DriverRegistry registry) =>
        registry.Register<ModbusUdpOptions>(
            ProtocolNames.ModbusUdp,
            static (options, device, context) => new ModbusUdpClient(options, device, context));

    /// <summary>Registers the Modbus ASCII (serial) driver for protocol name "ModbusAscii".</summary>
    public static DriverRegistry AddModbusAscii(this DriverRegistry registry) =>
        registry.Register<ModbusAsciiOptions>(
            ProtocolNames.ModbusAscii,
            static (options, device, context) => new ModbusAsciiClient(options, device, context));
}
