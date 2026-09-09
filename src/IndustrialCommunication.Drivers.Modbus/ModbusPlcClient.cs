using System.IO.Ports;
using System.Net.Sockets;
using IndustrialCommunication.Configuration;
using Microsoft.Extensions.Logging;
using NModbus;
using NModbus.Serial;

namespace IndustrialCommunication.Modbus;

/// <summary>
/// Modbus primitives shared by every transport (TCP / UDP / RTU / ASCII): area-kind checks and
/// the four Do* operations on top of a transport-owned <see cref="ModbusCommandLayer"/>.
/// </summary>
public abstract class ModbusClientBase : PlcClientBase
{
    protected ModbusClientBase(DeviceConfig device, DeviceRuntimeOptions runtime, DataLayout dataLayout,
        System.Text.Encoding stringEncoding, ILogger? logger)
        : base(device, runtime, dataLayout, stringEncoding, logger)
    {
    }

    /// <summary>The command layer of the current connection; null while disconnected.</summary>
    internal abstract ModbusCommandLayer? Layer { get; }

    protected override DeviceAddress ParseAddress(string address) => ModbusAddress.Parse(address);

    protected override async Task<CommResult> DoHeartbeatAsync(CancellationToken ct) =>
        (await DoReadBitsAsync(new DeviceAddress { Area = ModbusAreaMap.Coil, Offset = 0, IsBit = true }, 1, ct)
            .ConfigureAwait(false)).WithoutValue();

    protected override async Task<CommResult<bool[]>> DoReadBitsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        var layer = RequireLayer();
        var area = ModbusAreaMap.FromAreaName(address.Area);
        if (!ModbusAreaMap.IsBitArea(area))
            return CommResult<bool[]>.Fail(CommErrorKind.InvalidArgument, null,
                $"Area {address.Area} is a word area; use word operations.");
        return await layer.ReadBitsAsync(area, checked((ushort)address.Offset), count, ct).ConfigureAwait(false);
    }

    protected override async Task<CommResult> DoWriteBitsAsync(DeviceAddress address, IReadOnlyList<bool> values, CancellationToken ct)
    {
        var layer = RequireLayer();
        var area = ModbusAreaMap.FromAreaName(address.Area);
        if (!ModbusAreaMap.IsBitArea(area))
            return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                $"Area {address.Area} is a word area; use bit operations.");
        return await layer.WriteBitsAsync(area, checked((ushort)address.Offset), values, ct).ConfigureAwait(false);
    }

    protected override async Task<CommResult<ushort[]>> DoReadWordsAsync(DeviceAddress address, ushort count, CancellationToken ct)
    {
        var layer = RequireLayer();
        var area = ModbusAreaMap.FromAreaName(address.Area);
        if (ModbusAreaMap.IsBitArea(area))
            return CommResult<ushort[]>.Fail(CommErrorKind.InvalidArgument, null,
                $"Area {address.Area} is a bit area; use bit operations.");
        return await layer.ReadWordsAsync(area, checked((ushort)address.Offset), count, ct).ConfigureAwait(false);
    }

    protected override async Task<CommResult> DoWriteWordsAsync(DeviceAddress address, IReadOnlyList<ushort> values, CancellationToken ct)
    {
        var layer = RequireLayer();
        var area = ModbusAreaMap.FromAreaName(address.Area);
        if (ModbusAreaMap.IsBitArea(area))
            return CommResult.Fail(CommErrorKind.InvalidArgument, null,
                $"Area {address.Area} is a bit area; use bit operations.");
        return await layer.WriteWordsAsync(area, checked((ushort)address.Offset), values, ct).ConfigureAwait(false);
    }

    private ModbusCommandLayer RequireLayer() =>
        Layer ?? throw new InvalidOperationException($"[{Device.Name}] client is not connected.");
}

/// <summary>Modbus TCP transport (built on NModbus).</summary>
public sealed class ModbusTcpClient : ModbusClientBase
{
    private readonly ModbusTcpOptions _options;
    private TcpClient? _tcp;
    private IModbusMaster? _master;
    private ModbusCommandLayer? _layer;

    public ModbusTcpClient(ModbusTcpOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        _options = options;
    }

    internal override ModbusCommandLayer? Layer => _layer;

    protected override async Task DoConnectAsync(CancellationToken ct)
    {
        var tcp = new TcpClient();
        IModbusMaster? master = null;
        try
        {
            await tcp.ConnectAsync(_options.Ip, _options.Port, ct).ConfigureAwait(false);
            master = new ModbusFactory().CreateMaster(tcp);
        }
        catch
        {
            master?.Dispose();
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _master = master;
        _layer = new ModbusCommandLayer(master, _options.UnitId);
    }

    protected override Task DoDisconnectAsync()
    {
        var master = _master;
        var tcp = _tcp;
        _master = null;
        _tcp = null;
        _layer = null;

        master?.Dispose();
        tcp?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Modbus UDP transport (MBAP framing over UDP, built on NModbus).</summary>
public sealed class ModbusUdpClient : ModbusClientBase
{
    private readonly ModbusUdpOptions _options;
    private UdpClient? _udp;
    private IModbusMaster? _master;
    private ModbusCommandLayer? _layer;

    public ModbusUdpClient(ModbusUdpOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        _options = options;
    }

    internal override ModbusCommandLayer? Layer => _layer;

    protected override Task DoConnectAsync(CancellationToken ct)
    {
        var udp = new UdpClient();
        IModbusMaster? master = null;
        try
        {
            udp.Connect(_options.Ip, _options.Port);
            ct.ThrowIfCancellationRequested();
            master = new ModbusFactory().CreateMaster(udp);
        }
        catch
        {
            master?.Dispose();
            udp.Dispose();
            throw;
        }

        _udp = udp;
        _master = master;
        _layer = new ModbusCommandLayer(master, _options.UnitId);
        return Task.CompletedTask;
    }

    protected override Task DoDisconnectAsync()
    {
        var master = _master;
        var udp = _udp;
        _master = null;
        _udp = null;
        _layer = null;

        master?.Dispose();
        udp?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Modbus RTU transport over a serial port (built on NModbus + NModbus.Serial).</summary>
public sealed class ModbusRtuClient : ModbusClientBase
{
    private readonly ModbusRtuOptions _options;
    private SerialPort? _port;
    private IModbusMaster? _master;
    private ModbusCommandLayer? _layer;

    public ModbusRtuClient(ModbusRtuOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        _options = options;
    }

    internal override ModbusCommandLayer? Layer => _layer;

    protected override Task DoConnectAsync(CancellationToken ct)
    {
        var port = new SerialPort(
            _options.PortName,
            _options.BaudRate,
            _options.Parity,
            _options.DataBits,
            _options.StopBits)
        {
            ReadTimeout = Math.Max(100, Runtime.TimeoutMs),
            WriteTimeout = Math.Max(100, Runtime.TimeoutMs),
        };

        IModbusMaster? master = null;
        try
        {
            try
            {
                port.Open();
            }
            catch (UnauthorizedAccessException ex)
            {
                // missing/busy serial ports surface as UnauthorizedAccessException on Windows
                throw new IOException($"Cannot open serial port '{_options.PortName}': {ex.Message}", ex);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new IOException($"Invalid serial port settings for '{_options.PortName}': {ex.Message}", ex);
            }
            catch (ArgumentException ex)
            {
                // a port name that does not resolve to a device is an environment problem, not a coding one
                throw new IOException($"Cannot open serial port '{_options.PortName}': {ex.Message}", ex);
            }

            ct.ThrowIfCancellationRequested();
            master = new ModbusFactory().CreateRtuMaster(new SerialPortAdapter(port));
        }
        catch
        {
            master?.Dispose();
            port.Dispose();
            throw;
        }

        _port = port;
        _master = master;
        _layer = new ModbusCommandLayer(master, _options.UnitId, _options.RequestGapMs);
        return Task.CompletedTask;
    }

    protected override Task DoDisconnectAsync()
    {
        var master = _master;
        var port = _port;
        _master = null;
        _port = null;
        _layer = null;

        master?.Dispose();
        port?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Modbus ASCII transport over a serial port (built on NModbus + NModbus.Serial).</summary>
public sealed class ModbusAsciiClient : ModbusClientBase
{
    private readonly ModbusAsciiOptions _options;
    private SerialPort? _port;
    private IModbusMaster? _master;
    private ModbusCommandLayer? _layer;

    public ModbusAsciiClient(ModbusAsciiOptions options, DeviceConfig device, ClientBuildContext context)
        : base(device, context.Runtime, options.DataLayout, options.ResolveEncoding(),
            context.LoggerFactory.CreateLogger(device.Name))
    {
        _options = options;
    }

    internal override ModbusCommandLayer? Layer => _layer;

    protected override Task DoConnectAsync(CancellationToken ct)
    {
        var port = new SerialPort(
            _options.PortName,
            _options.BaudRate,
            _options.Parity,
            _options.DataBits,
            _options.StopBits)
        {
            ReadTimeout = Math.Max(100, Runtime.TimeoutMs),
            WriteTimeout = Math.Max(100, Runtime.TimeoutMs),
        };

        IModbusMaster? master = null;
        try
        {
            try
            {
                port.Open();
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException($"Cannot open serial port '{_options.PortName}': {ex.Message}", ex);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new IOException($"Invalid serial port settings for '{_options.PortName}': {ex.Message}", ex);
            }
            catch (ArgumentException ex)
            {
                throw new IOException($"Cannot open serial port '{_options.PortName}': {ex.Message}", ex);
            }

            ct.ThrowIfCancellationRequested();
            master = new ModbusFactory().CreateAsciiMaster(new SerialPortAdapter(port));
        }
        catch
        {
            master?.Dispose();
            port.Dispose();
            throw;
        }

        _port = port;
        _master = master;
        _layer = new ModbusCommandLayer(master, _options.UnitId, _options.RequestGapMs);
        return Task.CompletedTask;
    }

    protected override Task DoDisconnectAsync()
    {
        var master = _master;
        var port = _port;
        _master = null;
        _port = null;
        _layer = null;

        master?.Dispose();
        port?.Dispose();
        return Task.CompletedTask;
    }
}
