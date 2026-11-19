using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SteadyDim;

internal sealed class MonitorConfig
{
    public string Name { get; set; } = "";
    public int Brightness { get; set; } = 50;
    public int GammaTrim { get; set; }
}

internal sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 2;
    public bool DeepDimEnabled { get; set; }
    public int DeepDimPercent { get; set; } = 80;
    public bool StartWithWindows { get; set; } = true;
    public int? SharedHardwareBrightness { get; set; }
    public bool PauseDeepDimWhilePanelOpen { get; set; } = true;
    public Dictionary<string, MonitorConfig> Monitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNew { get; set; }
}

internal static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppConfig Load()
    {
        var config = new AppConfig();

        try
        {
            if (!File.Exists(AppInfo.ConfigPath))
            {
                config.IsNew = true;
                Normalize(config);
                return config;
            }

            var json = File.ReadAllText(AppInfo.ConfigPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (loaded is null)
            {
                throw new InvalidDataException("Config file deserialized to null.");
            }

            config = loaded;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read config; starting with defaults.", ex);
            config = new AppConfig { IsNew = true };

            try
            {
                if (File.Exists(AppInfo.ConfigPath))
                {
                    File.Copy(AppInfo.ConfigPath, AppInfo.ConfigPath + ".bad", overwrite: true);
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        Normalize(config);
        return config;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Normalize(config);
            Directory.CreateDirectory(AppInfo.DataDir);

            var json = JsonSerializer.Serialize(config, JsonOptions);
            var temp = AppInfo.ConfigPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppInfo.ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save config.", ex);
        }
    }

    private static void Normalize(AppConfig config)
    {
        config.SchemaVersion = 2;
        config.DeepDimPercent = Math.Clamp(config.DeepDimPercent, 0, 90);
        config.Monitors ??= new Dictionary<string, MonitorConfig>(StringComparer.OrdinalIgnoreCase);

        if (config.SharedHardwareBrightness is null)
        {
            var values = config.Monitors.Values
                .Select(m => Math.Clamp(m.Brightness, 0, 100))
                .ToList();

            config.SharedHardwareBrightness = values.Count > 0
                ? (int)Math.Round(values.Average())
                : 50;
        }

        config.SharedHardwareBrightness = Math.Clamp(config.SharedHardwareBrightness.Value, 0, 100);

        foreach (var pair in config.Monitors)
        {
            pair.Value.Brightness = config.SharedHardwareBrightness.Value;
            pair.Value.GammaTrim = Math.Clamp(pair.Value.GammaTrim, 0, 50);
            pair.Value.Name ??= "";
        }
    }
}
