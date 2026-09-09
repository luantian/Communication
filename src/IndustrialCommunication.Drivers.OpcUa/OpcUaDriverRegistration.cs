using IndustrialCommunication.Configuration;
using IndustrialCommunication.OpcUa;

namespace IndustrialCommunication;

public static class OpcUaDriverRegistration
{
    /// <summary>Registers the OPC UA driver (official OPC Foundation stack) for protocol name "OpcUa".
    /// Addresses are NodeId strings, e.g. "ns=2;s=PumpSpeed".</summary>
    public static DriverRegistry AddOpcUa(this DriverRegistry registry) =>
        registry.Register<OpcUaOptions>(
            ProtocolNames.OpcUa,
            static (options, device, context) => new OpcUaPlcClient(options, device, context));
}
