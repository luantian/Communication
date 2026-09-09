using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using IndustrialCommunication.OpcUa;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

/// <summary>
/// Runs against a real OPC UA server when INDUSTRIALCOMM_OPCUA_TEST_URL is set (e.g. the open62541
/// demo server or a PLC's built-in server). Skipped otherwise.
/// </summary>
public sealed class OpcUaHardwareTests
{
    [SkippableFact]
    public async Task Native_subscription_delivers_changes_without_polling()
    {
        var url = Environment.GetEnvironmentVariable("INDUSTRIALCOMM_OPCUA_TEST_URL");
        Skip.If(string.IsNullOrWhiteSpace(url), "Set INDUSTRIALCOMM_OPCUA_TEST_URL (e.g. opc.tcp://192.168.1.50:4840) to run OPC UA hardware tests.");

        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": 5000, "connectRetries": 0 },
                  "devices": [ { "name": "ua", "protocol": "OpcUa",
                                 "connection": { "endpointUrl": "{{url}}" } } ]
                }
                """),
            r => r.AddOpcUa());

        await using (host)
        {
            var client = (OpcUaPlcClient)host.GetClient("ua");
            await client.ConnectAsync();

            var tcs = new TaskCompletionSource<OpcUaValueChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = await client.CreateSubscriptionAsync(
                (_, e) => tcs.TrySetResult(e),
                publishingIntervalMs: 500);

            // Current time node exists on every compliant server and ticks constantly.
            subscription.Subscribe("i=2258");
            await subscription.ApplyChangesAsync();

            var done = await Task.WhenAny(tcs.Task, Task.Delay(10_000));
            Assert.Same(tcs.Task, done);
            Assert.True(tcs.Task.Result.GoodQuality);
            Assert.NotNull(tcs.Task.Result.Value);
        }
    }
}
