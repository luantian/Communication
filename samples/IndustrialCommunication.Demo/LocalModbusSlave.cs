using System.Net;
using System.Net.Sockets;
using NModbus;
using NModbus.Data;

namespace IndustrialCommunication.Demo;

/// <summary>In-process Modbus TCP slave so the demo runs without any hardware.</summary>
public sealed class LocalModbusSlave : IAsyncDisposable
{
    private const int Port = 15020;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;

    public IPointSource<ushort> HoldingRegisters { get; } = new PointSource<ushort>();
    public IPointSource<ushort> InputRegisters { get; } = new PointSource<ushort>();
    public IPointSource<bool> Coils { get; } = new PointSource<bool>();
    public IPointSource<bool> DiscreteInputs { get; } = new PointSource<bool>();

    public static int SlavePort => Port;

    public LocalModbusSlave()
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();

        var store = new DemoDataStore(this);
        var factory = new ModbusFactory();
        var network = factory.CreateSlaveNetwork(_listener);
        network.AddSlave(factory.CreateSlave(1, store));
        _listenTask = network.ListenAsync(_cts.Token);

        // Preset some demo values.
        HoldingRegisters.WritePoints(10, [0x1234, 0x5678]);
        HoldingRegisters.WritePoints(30, [0x3F80, 0x0000]); // float 1.0 in ABCD layout
        HoldingRegisters.WritePoints(50, [42]);
        Coils.WritePoints(2, [false, true, false, true]);
        InputRegisters.WritePoints(20, [0x00AA]);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _listenTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException)
        {
            // stopping the listener aborts pending accepts — expected on shutdown
        }
        _cts.Dispose();
    }

    private sealed class DemoDataStore(LocalModbusSlave owner) : ISlaveDataStore
    {
        public IPointSource<ushort> HoldingRegisters => owner.HoldingRegisters;
        public IPointSource<ushort> InputRegisters => owner.InputRegisters;
        public IPointSource<bool> CoilDiscretes => owner.Coils;
        public IPointSource<bool> CoilInputs => owner.DiscreteInputs;
    }
}
