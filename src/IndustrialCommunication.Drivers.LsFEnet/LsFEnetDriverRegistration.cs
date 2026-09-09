using IndustrialCommunication.Configuration;
using IndustrialCommunication.LsFEnet;

namespace IndustrialCommunication;

public static class LsFEnetDriverRegistration
{
    /// <summary>Registers the LS Electric XGB/XGK dedicated-protocol driver (FEnet TCP 2004) for protocol name "LsFEnet".</summary>
    public static DriverRegistry AddLsFEnet(this DriverRegistry registry) =>
        registry.Register<LsFEnetOptions>(
            ProtocolNames.LsFEnet,
            static (options, device, context) => new LsFEnetClient(options, device, context));
}
