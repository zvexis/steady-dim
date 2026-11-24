using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SteadyDim;

internal sealed class MonitorConfig
{
    public string Name { get; set; } = "";
    public int Brightness { get; set; }
}

internal sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 5;
    public int HardwareMaster { get; set; }

    /// <summary>Global software dim reduction, 0–90%.</summary>
    public int SoftwarePercent { get; set; }

    public bool StartWithWindows { get; set; } = true;
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
        config.SchemaVersion = 5;
        config.HardwareMaster = Math.Clamp(config.HardwareMaster, 0, 100);
        config.SoftwarePercent = Math.Clamp(config.SoftwarePercent, 0, 90);
        config.Monitors ??= new Dictionary<string, MonitorConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in config.Monitors)
        {
            pair.Value.Name ??= "";
            pair.Value.Brightness = Math.Clamp(pair.Value.Brightness, 0, 100);
        }
    }
}
