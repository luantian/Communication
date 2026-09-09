using NModbus;
using NModbus.Data;

namespace IndustrialCommunication.IntegrationTests.TestSupport;

/// <summary>Presettable slave data store for the in-process NModbus TCP slave.</summary>
public sealed class TestSlaveDataStore : ISlaveDataStore
{
    public IPointSource<ushort> HoldingRegisters { get; } = new PointSource<ushort>();
    public IPointSource<ushort> InputRegisters { get; } = new PointSource<ushort>();
    public IPointSource<bool> CoilDiscretes { get; } = new PointSource<bool>();
    public IPointSource<bool> CoilInputs { get; } = new PointSource<bool>();
}
