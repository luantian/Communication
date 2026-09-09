using System.Net;
using System.Text;
using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.Demo;
using IndustrialCommunication.Mqtt;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;

Console.OutputEncoding = System.Text.Encoding.UTF8;

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(o => o.SingleLine = true));

// The demo spins up an in-process Modbus TCP slave so everything below runs without hardware.
await using var slave = new LocalModbusSlave();

// The MQTT section runs against an in-process broker (MQTTnet server) — also hardware-free.
var mqttPort = GetFreePort();
var mqttServer = new MqttFactory().CreateMqttServer(
    new MQTTnet.Server.MqttServerOptionsBuilder()
        .WithDefaultEndpoint()
        .WithDefaultEndpointPort(mqttPort)
        .Build());
await mqttServer.StartAsync();

await using var host = CommunicationHost.Load(
    Path.Combine(AppContext.BaseDirectory, "configs", "sample.json"),
    r => r.AddModbusTcp().AddModbusUdp().AddModbusRtu().AddModbusAscii()
          .AddSiemensS7().AddMitsubishiMc().AddMitsubishiMcUdp()
          .AddOmronFins().AddOmronFinsUdp()
          .AddPanasonicMewtocol().AddKeyenceUpperLink().AddKeyenceUpperLinkUdp()
          .AddRockwellEtherNetIp().AddGeSrtp().AddLsFEnet().AddOpcUa(),
    loggerFactory);

var plc = host.GetClient("demo-slave");
plc.ConnectionStateChanged += (_, e) => Console.WriteLine($"  [event] {e.State} {e.Reason ?? ""}");

Console.WriteLine("== connect ==");
var connectResults = await host.ConnectAllAsync();
foreach (var (name, result) in connectResults)
    Console.WriteLine($"  {name}: {(result.Success ? "OK" : result)}");

Console.WriteLine();
Console.WriteLine("== word read: HR10, 2 words ==");
var words = await plc.ReadWordsAsync("HR10", 2);
Console.WriteLine($"  {(words.Success ? string.Join(", ", words.Value.Select(w => $"0x{w:X4}")) : words)}");

Console.WriteLine();
Console.WriteLine("== typed reads ==");
var floatValue = await plc.ReadAsync<float>("HR30");   // ABCD layout
var intValue = await plc.ReadAsync<int>("HR10");
var boolValue = await plc.ReadAsync<bool>("C3");
Console.WriteLine($"  float HR30 = {floatValue.Value}");
Console.WriteLine($"  int   HR10 = 0x{intValue.Value:X8}");
Console.WriteLine($"  bool  C3   = {boolValue.Value}");

Console.WriteLine();
Console.WriteLine("== write then read back ==");
await plc.WriteAsync<short>("HR60", -1234);
var back = await plc.ReadAsync<short>("HR60");
Console.WriteLine($"  wrote -1234 to HR60, read back {back.Value}");

await plc.WriteStringAsync("HR200", "PUMP-1", 8);
var text = await plc.ReadStringAsync("HR200", 8);
Console.WriteLine($"  string HR200 = '{text.Value}'");

Console.WriteLine();
Console.WriteLine("== group read (per-point status) ==");
var group = await plc.ReadGroupAsync(
[
    new DevicePoint { Name = "count", Address = "HR50", ValueType = PlcValueType.UInt16 },
    new DevicePoint { Name = "speed", Address = "HR30", ValueType = PlcValueType.Float32 },
    new DevicePoint { Name = "run", Address = "C3", ValueType = PlcValueType.Bit },
    new DevicePoint { Name = "bad", Address = "WHERE", ValueType = PlcValueType.UInt16 },
]);
Console.WriteLine($"  count = {group.GetValue<ushort>("count")}");
Console.WriteLine($"  speed = {group.GetValue<float>("speed"):F1}");
Console.WriteLine($"  run   = {group.GetValue<bool>("run")}");
Console.WriteLine($"  bad   = {group.Statuses["bad"].Kind}: {group.Statuses["bad"].Message}");

Console.WriteLine();
Console.WriteLine("== error handling: invalid address ==");
var invalid = await plc.ReadWordsAsync("NOSUCH", 1);
Console.WriteLine($"  {invalid}");

Console.WriteLine();
Console.WriteLine("== polling engine + subscription (pollGroups in config) ==");
var mutator = Task.Run(async () =>
{
    for (var i = 0; i < 3; i++)
    {
        await Task.Delay(700);
        await plc.WriteAsync<short>("HR60", (short)(i + 1));
    }
});

await using (var engine = host.CreatePollingEngine())
{
    var changes = 0;
    using var subscription = engine.Subscribe("tank", v =>
    {
        Interlocked.Increment(ref changes);
        Console.WriteLine($"  [subscribe] tank -> {v}");
    });
    engine.PollCompleted += (_, e) =>
        Console.WriteLine($"  [poll] {e.GroupName}: {(e.Result.AllSuccess ? "ok" : "partial")}, changed: {e.Changed.Count}");
    await engine.StartAsync();
    await mutator;
    await Task.Delay(300); // let the last poll cycle notice the final value
    Console.WriteLine($"  subscription fired {changes} time(s)");
}

Console.WriteLine();
Console.WriteLine("== MQTT bridge (in-process broker) ==");
var bridgeOptions = new MqttBridgeOptions { Host = "127.0.0.1", Port = mqttPort, ClientId = "demo-bridge", TopicPrefix = "plant1" };
await using (var bridge = new MqttBridge(host, bridgeOptions, loggerFactory))
{
    // An observer client subscribes to everything the bridge publishes.
    var observer = new MqttFactory().CreateMqttClient();
    await observer.ConnectAsync(new MqttClientOptionsBuilder()
        .WithTcpServer("127.0.0.1", mqttPort).WithClientId("demo-observer").Build());
    var published = 0;
    observer.ApplicationMessageReceivedAsync += e =>
    {
        if (e.ApplicationMessage.Topic.EndsWith("/groups/main", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref published);
            Console.WriteLine($"  [mqtt] {e.ApplicationMessage.Topic}: {Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray())}");
        }
        return Task.CompletedTask;
    };
    await observer.SubscribeAsync("plant1/#");

    await using var engine = host.CreatePollingEngine();
    bridge.Attach(engine);
    await bridge.StartAsync();
    await engine.StartAsync();

    // change a value so a poll cycle publishes it
    await plc.WriteAsync<short>("HR60", 777);
    var deadline = Environment.TickCount64 + 5000;
    while (Volatile.Read(ref published) == 0 && Environment.TickCount64 < deadline)
        await Task.Delay(50);

    // send a write command over MQTT and verify the device applied it
    await observer.PublishAsync(new MqttApplicationMessageBuilder()
        .WithTopic("plant1/commands/write")
        .WithPayload("""{ "device": "demo-slave", "address": "HR70", "value": 4242, "type": "UInt16" }""")
        .Build());
    await Task.Delay(500);
    var cmdValue = await plc.ReadWordsAsync("HR70", 1);
    Console.WriteLine($"  [mqtt] command wrote HR70 = {cmdValue.Value[0]}");

    await engine.StopAsync();
}

Console.WriteLine();
Console.WriteLine("== done ==");
await mqttServer.StopAsync(new MQTTnet.Server.MqttServerStopOptions());
mqttServer.Dispose();

static int GetFreePort()
{
    var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    socket.Start();
    var port = ((IPEndPoint)socket.LocalEndpoint).Port;
    socket.Stop();
    return port;
}
