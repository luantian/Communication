using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;

namespace IndustrialCommunication;

/// <summary>Handed to driver factories when a client is constructed.</summary>
public sealed class ClientBuildContext
{
    public required DeviceRuntimeOptions Runtime { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
}
