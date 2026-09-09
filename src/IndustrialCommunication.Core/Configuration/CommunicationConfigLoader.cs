using System.Text.Json;
using System.Text.Json.Serialization;

namespace IndustrialCommunication.Configuration;

/// <summary>Loads <see cref="CommunicationConfig"/> from JSON (case-insensitive, enums as strings, comments allowed).</summary>
public static class CommunicationConfigLoader
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static CommunicationConfig Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Config path must not be empty.", nameof(path));
        if (!File.Exists(path))
            throw new CommunicationException($"Configuration file not found: {Path.GetFullPath(path)}");

        return Parse(File.ReadAllText(path));
    }

    public static CommunicationConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new CommunicationException("Configuration is empty.");

        CommunicationConfig config;
        try
        {
            config = JsonSerializer.Deserialize<CommunicationConfig>(json, JsonOptions)
                ?? throw new CommunicationException("Configuration deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new CommunicationException($"Invalid configuration JSON: {ex.Message}");
        }

        return CommunicationConfig.ValidateOrThrow(config);
    }
}
