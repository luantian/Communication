using IndustrialCommunication.Configuration;
using IndustrialCommunication.Siemens;

namespace IndustrialCommunication;

public static class S7DriverRegistration
{
    /// <summary>Registers the Siemens S7 driver (S7-200 SMART / 300 / 400 / 1200 / 1500) for protocol name "S7".</summary>
    public static DriverRegistry AddSiemensS7(this DriverRegistry registry) =>
        registry.Register<S7Options>(
            ProtocolNames.SiemensS7,
            static (options, device, context) => new S7PlcClient(options, device, context));
}
