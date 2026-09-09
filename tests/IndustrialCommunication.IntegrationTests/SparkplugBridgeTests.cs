using System.Net;
using System.Text;
using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using IndustrialCommunication.Mqtt;
using IndustrialCommunication.Mqtt.Sparkplug;
using IndustrialCommunication.Tests.TestSupport;
using MQTTnet;
using MQTTnet.Client;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

public sealed class SparkplugBridgeTests : IAsyncDisposable
{
    private readonly MQTTnet.Server.MqttServer _server;
    private readonly int _port;

    public SparkplugBridgeTests()
    {
        _port = GetFreePort();
        _server = new MqttFactory().CreateMqttServer(
            new MQTTnet.Server.MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(_port)
                .Build());
        _ = _server.StartAsync();
    }

    private static int GetFreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private static async Task UntilAsync(Func<bool> probe, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (probe())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition was not met in time.");
    }

    [Fact]
    public async Task Births_ddata_and_dcmd_roundtrip()
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse("""
                {
                  "defaults": { "connectRetries": 0 },
                  "devices": [ { "name": "pump1", "protocol": "Fake", "connection": {} } ],
                  "pollGroups": [ { "name": "pump1", "device": "pump1", "intervalMs": 20,
                                     "points": [ { "name": "W:10", "address": "W:10", "valueType": "UInt16" } ] } ]
                }
                """),
            r => r.Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));
        await using (host)
        {
            var client = (FakePlcClient)host.GetClient("pump1");
            var mqtt = new MqttBridgeOptions { Host = "127.0.0.1", Port = _port, ClientId = "sparkplug-under-test" };
            var sparkplug = new SparkplugBridgeOptions { Group = "plant", EdgeNode = "edge01", Devices = ["pump1"] };
            await using var bridge = new SparkplugBridge(host, mqtt, sparkplug);

            var topics = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var observer = new MqttFactory().CreateMqttClient();
            await observer.ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", _port).WithClientId("sparkplug-observer").Build());
            observer.ApplicationMessageReceivedAsync += e =>
            {
                topics.Enqueue(e.ApplicationMessage.Topic);
                return Task.CompletedTask;
            };
            await observer.SubscribeAsync("spBv1.0/#");

            await bridge.StartAsync();

            // NBIRTH + DBIRTH for the configured device
            await UntilAsync(() => topics.Contains("spBv1.0/plant/NBIRTH/edge01"));
            await UntilAsync(() => topics.Contains("spBv1.0/plant/DBIRTH/edge01/pump1"));

            // data changes flow as DDATA on the device topic
            var bump = 0;
            client.WordValues = (a, c) => a.Offset == 10 ? [(ushort)++bump] : null;
            await using var engine = host.CreatePollingEngine();
            bridge.Attach(engine);
            await engine.StartAsync();
            await UntilAsync(() => bump > 0 && topics.Contains("spBv1.0/plant/DDATA/edge01/pump1"));

            // DCMD write lands on the device (metric name is the point address)
            client.WordValues = null; // fall through to written values
            await observer.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic("spBv1.0/plant/DCMD/edge01/pump1")
                .WithPayload(SparkplugProto.EncodePayload(null,
                    [new SparkplugMetric { Name = "W:20", DataType = SparkplugDataType.Int64, LongValue = 777 }],
                    seq: 0))
                .Build());
            var written = false;
            string lastRead = "";
            var deadline = Environment.TickCount64 + 8000;
            while (!written && Environment.TickCount64 < deadline)
            {
                await Task.Delay(50);
                var read = await host.GetClient("pump1").ReadAsync<long>("W:20");
                lastRead = read.ToString();
                written = read.Success && read.Value == 777;
            }
            Assert.True(written, $"The DCMD write never reached the device. LastRead: {lastRead}. Observer saw: {string.Join(", ", topics)}");

            await engine.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync(new MQTTnet.Server.MqttServerStopOptions());
        _server.Dispose();
    }
}
