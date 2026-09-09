using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using Xunit;

namespace IndustrialCommunication.IntegrationTests;

/// <summary>
/// Runs against a real S7 PLC when the environment variable INDUSTRIALCOMM_S7_TEST_IP is set
/// (e.g. a Snap7 server or a real CPU). Skipped otherwise.
/// Optional variables: INDUSTRIALCOMM_S7_TEST_CPU (default S71200),
/// INDUSTRIALCOMM_S7_TEST_RACK (default 0), INDUSTRIALCOMM_S7_TEST_SLOT (default 0).
/// </summary>
public sealed class S7HardwareTests
{
    [SkippableFact]
    public async Task Write_then_read_roundtrip_on_merker_word()
    {
        var ip = Environment.GetEnvironmentVariable("INDUSTRIALCOMM_S7_TEST_IP");
        Skip.If(string.IsNullOrWhiteSpace(ip), "Set INDUSTRIALCOMM_S7_TEST_IP to run S7 hardware tests.");

        var cpu = Environment.GetEnvironmentVariable("INDUSTRIALCOMM_S7_TEST_CPU") ?? "S71200";
        var rack = Environment.GetEnvironmentVariable("INDUSTRIALCOMM_S7_TEST_RACK") ?? "0";
        var slot = Environment.GetEnvironmentVariable("INDUSTRIALCOMM_S7_TEST_SLOT") ?? "0";

        var host = CommunicationHost.FromConfig(
            CommunicationConfigLoader.Parse($$"""
                {
                  "defaults": { "timeoutMs": 3000, "connectRetries": 0 },
                  "devices": [ { "name": "s7", "protocol": "S7",
                                 "connection": { "ip": "{{ip}}", "cpuType": "{{cpu}}",
                                                 "rack": {{rack}}, "slot": {{slot}} } } ]
                }
                """),
            r => r.AddSiemensS7());

        await using (host)
        {
            var plc = host.GetClient("s7");

            Assert.True((await plc.WriteAsync<short>("MW100", -4242)).Success);
            Assert.Equal(-4242, (await plc.ReadAsync<short>("MW100")).Value);

            Assert.True((await plc.WriteAsync<bool>("M50.4", true)).Success);
            Assert.True((await plc.ReadAsync<bool>("M50.4")).Value);
        }
    }
}
