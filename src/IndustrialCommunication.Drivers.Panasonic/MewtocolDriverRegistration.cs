using IndustrialCommunication.Configuration;
using IndustrialCommunication.Panasonic;

namespace IndustrialCommunication;

public static class MewtocolDriverRegistration
{
    /// <summary>Registers the Panasonic FP series MEWTOCOL-COM driver (TCP, default port 9094) for protocol name "PanasonicMewtocol".</summary>
    public static DriverRegistry AddPanasonicMewtocol(this DriverRegistry registry) =>
        registry.Register<MewtocolOptions>(
            ProtocolNames.PanasonicMewtocol,
            static (options, device, context) => new MewtocolClient(options, device, context));
}
