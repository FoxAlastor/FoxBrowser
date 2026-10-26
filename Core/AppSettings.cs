using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxBrowser.Core;

public sealed class AppSettings
{
    public string AppDisplayName { get; set; } = "FoxBrowser";
    public string? AppIconPath { get; set; }
    public Dictionary<string, string> Shortcuts { get; set; } = new();
    public string AddressBarHideMode { get; set; } = "Manual";
    public int AddressBarHideSeconds { get; set; } = 5;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public bool RememberTopPanelState { get; set; }
    public bool TopPanelHidden { get; set; }
    public bool AddressBarHidden { get; set; }
}

public static class AppSettingsStore
{
    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
            if (settings is null) return new AppSettings();
            if (string.IsNullOrWhiteSpace(settings.AppDisplayName)) settings.AppDisplayName = "FoxBrowser";
            if (!string.IsNullOrWhiteSpace(settings.AppIconPath) && !File.Exists(settings.AppIconPath))
                settings.AppIconPath = null;
            settings.AddressBarHideMode = string.Equals(settings.AddressBarHideMode, "Timer", StringComparison.OrdinalIgnoreCase) ? "Timer" : "Manual";
            settings.AddressBarHideSeconds = Math.Clamp(settings.AddressBarHideSeconds, 2, 60);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(AppContext.BaseDirectory);
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
    }

    /// Load, mutate and save settings under one lock. Returns the current settings object.
    public static AppSettings Update(Action<AppSettings> mutate)
    {
        lock (Gate)
        {
            var settings = Load();
            mutate(settings);
            Directory.CreateDirectory(AppContext.BaseDirectory);
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, SettingsPath, overwrite: true);
            return settings;
        }
    }
}
