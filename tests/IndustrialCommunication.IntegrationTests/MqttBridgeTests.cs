using System.Collections.Concurrent;
using System.Net;
using System.Text;
using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.IntegrationTests.TestSupport;
using IndustrialCommunication.Mqtt;
using IndustrialCommunication.Tests.TestSupport;
using MQTTnet;
using MQTTnet.Client;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

public sealed class MqttBridgeTests : IAsyncDisposable
{
    private readonly MQTTnet.Server.MqttServer _server;
    private readonly int _port;

    public MqttBridgeTests()
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

    private sealed record Msg(string Topic, string Payload);

    private static async Task<Msg> UntilAsync(Func<Msg?> probe, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (probe() is { } value)
                return value;
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition was not met in time.");
    }

    [Fact]
    public async Task Poll_changes_are_published_and_commands_write_back()
    {
        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse("""
                {
                  "defaults": { "connectRetries": 0 },
                  "devices": [ { "name": "fake", "protocol": "Fake", "connection": {} } ],
                  "pollGroups": [ { "name": "g1", "device": "fake", "intervalMs": 20,
                                     "points": [ { "name": "tank", "address": "W:10", "valueType": "UInt16" } ] } ]
                }
                """),
            r => r.Register<FakeOptions>("Fake", (o, d, c) => new FakePlcClient(o, d, c)));
        await using (host)
        {
            var client = (FakePlcClient)host.GetClient("fake");
            var bridgeOptions = new MqttBridgeOptions { Host = "127.0.0.1", Port = _port, ClientId = "bridge-under-test", TopicPrefix = "ind" };
            await using var bridge = new MqttBridge(host, bridgeOptions);

            // Observer: subscribes to everything and records messages.
            var messages = new ConcurrentQueue<Msg>();
            var observer = new MqttFactory().CreateMqttClient();
            await observer.ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", _port).WithClientId("observer").Build());
            observer.ApplicationMessageReceivedAsync += e =>
            {
                messages.Enqueue(new Msg(e.ApplicationMessage.Topic,
                    Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray())));
                return Task.CompletedTask;
            };
            await observer.SubscribeAsync("ind/#");

            // Publishing path: poll engine → bridge → MQTT.
            var bump = 0;
            client.WordValues = (a, c) => a.Offset == 10 ? [(ushort)++bump] : null;
            await using var engine = host.CreatePollingEngine();
            bridge.Attach(engine);
            await bridge.StartAsync();
            await engine.StartAsync();

            var first = await UntilAsync(() =>
                messages.FirstOrDefault(m => m.Topic == "ind/groups/g1" && m.Payload.Contains("\"tank\"")));
            Assert.Contains("\"changed\"", first.Payload);
            Assert.Contains("\"tank\":1", first.Payload.Replace(" ", ""));

            // Command path: MQTT → bridge → device.
            await observer.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic("ind/commands/write")
                .WithPayload("""{ "device": "fake", "address": "W:20", "value": 42, "type": "UInt16" }""")
                .Build());
            var result = await UntilAsync(() => messages.FirstOrDefault(m => m.Topic == "ind/commands/result"));
            Assert.Contains("\"success\":true", result.Payload.Replace(" ", ""));

            var written = await host.GetClient("fake").ReadWordsAsync("W:20", 1);
            Assert.True(written.Success, written.ToString());
            Assert.Equal((ushort)42, written.Value[0]);

            await engine.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync(new MQTTnet.Server.MqttServerStopOptions());
        _server.Dispose();
    }
}
