using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinPanX.Agent.Configuration;

public static class WinPanXConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    static WinPanXConfigLoader()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static WinPanXConfig LoadOrCreateDefault(string path)
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<WinPanXConfig>(json, JsonOptions)
                ?? new WinPanXConfig();
            config.Validate();
            return config;
        }

        var defaultConfig = new WinPanXConfig();
        defaultConfig.Validate();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var defaultJson = JsonSerializer.Serialize(defaultConfig, JsonOptions);
        File.WriteAllText(path, defaultJson);
        return defaultConfig;
    }
}

