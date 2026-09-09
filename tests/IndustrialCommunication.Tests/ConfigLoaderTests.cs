using IndustrialCommunication;
using IndustrialCommunication.Configuration;
using Xunit;

namespace IndustrialCommunication.Tests;

public class ConfigLoaderTests
{
    [Fact]
    public void Parse_loads_defaults_and_devices()
    {
        var config = CommunicationConfigLoader.Parse("""
            {
              "defaults": { "timeoutMs": 1500, "connectRetries": 0, "autoReconnect": false },
              "devices": [
                { "name": "plc-a", "protocol": "Fake", "connection": { "token": "abc" } },
                { "name": "plc-b", "protocol": "Fake", "enabled": false }
              ]
            }
            """);

        Assert.Equal(1500, config.Defaults.TimeoutMs);
        Assert.Equal(0, config.Defaults.ConnectRetries);
        Assert.False(config.Defaults.AutoReconnect);
        Assert.Equal(2, config.Devices.Count);
        Assert.Equal("plc-a", config.Devices[0].Name);
        Assert.Equal("Fake", config.Devices[0].Protocol);
        Assert.False(config.Devices[1].Enabled);
    }

    [Fact]
    public void Parse_accepts_comments_trailing_commas_and_upper_case_properties()
    {
        var config = CommunicationConfigLoader.Parse("""
            // site configuration
            {
              "Defaults": { "TimeoutMs": 777, },
              "Devices": [ { "Name": "d1", "Protocol": "Fake" } ]
            }
            """);

        Assert.Equal(777, config.Defaults.TimeoutMs);
        Assert.Equal("d1", config.Devices[0].Name);
    }

    [Fact]
    public void Parse_rejects_duplicate_names_case_insensitively()
    {
        Assert.Throws<CommunicationException>(() => CommunicationConfigLoader.Parse(
            """{ "devices": [ { "name": "a", "protocol": "F" }, { "name": "A", "protocol": "F" } ] }"""));
    }

    [Fact]
    public void Parse_rejects_empty_name_or_protocol()
    {
        Assert.Throws<CommunicationException>(() => CommunicationConfigLoader.Parse(
            """{ "devices": [ { "name": "", "protocol": "F" } ] }"""));
        Assert.Throws<CommunicationException>(() => CommunicationConfigLoader.Parse(
            """{ "devices": [ { "name": "a", "protocol": "" } ] }"""));
    }

    [Fact]
    public void Parse_rejects_broken_json_with_clear_message()
    {
        var ex = Assert.Throws<CommunicationException>(() => CommunicationConfigLoader.Parse("{ not json"));
        Assert.Contains("Invalid configuration JSON", ex.Message);
    }

    [Fact]
    public void Load_missing_file_throws()
    {
        var ex = Assert.Throws<CommunicationException>(() => CommunicationConfigLoader.Load("no-such-file.json"));
        Assert.Contains("not found", ex.Message);
    }
}
