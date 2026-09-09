using System.Net;
using System.Net.Sockets;
using NModbus;
using NModbus.Data;

namespace IndustrialCommunication.IntegrationTests.TestSupport;

/// <summary>Starts an in-process NModbus TCP slave on a random loopback port.</summary>
public sealed class ModbusSlaveFixture : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;

    public TestSlaveDataStore DataStore { get; } = new();
    public int Port { get; }

    public ModbusSlaveFixture()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var factory = new ModbusFactory();
        var network = factory.CreateSlaveNetwork(_listener);
        network.AddSlave(factory.CreateSlave(1, DataStore));
        _listenTask = network.ListenAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _listenTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException)
        {
            // stopping the listener aborts pending accepts — expected on shutdown
        }

        _cts.Dispose();
        _listener.Stop();
    }
}
