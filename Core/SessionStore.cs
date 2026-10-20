using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxBrowser.Core;

/// <summary>
/// Snapshot of a single tab persisted to disk.
/// All properties are nullable so old session files remain forward-compatible.
/// </summary>
public sealed class TabSnapshot
{
    public string  Address       { get; init; } = "https://www.google.com";
    public string  Title         { get; init; } = "Нова вкладка";
    public bool    IsAudioOnly   { get; init; }
    public bool    IsDarkMode    { get; init; }
    public double  DarkBrightness { get; init; } = 0.72;
    public double  DarkContrast   { get; init; } = 1.08;
    public double  DarkSaturation { get; init; } = 0.88;
    public double  ScrollY       { get; init; }
    public double  MediaPosition { get; init; }
    public bool    WasPlaying    { get; init; }
    public string? CustomTitle   { get; init; }
    public string? ColorHex      { get; init; }
}

public sealed class Session
{
    public List<TabSnapshot> Tabs          { get; init; } = new();
    public int               ActiveTabIndex { get; init; }
}

/// <summary>
/// Saves and loads the browser session (open tabs + per-tab state) to a JSON
/// file in the portable FoxBrowser folder next to the executable.
///
/// Crash-recovery strategy: the file is re-written after every successful
/// NavigationCompleted so a hard crash never loses more than one page load.
/// </summary>
public static class SessionStore
{
    private static readonly string SessionPath = Path.Combine(AppContext.BaseDirectory, "session.json");
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented         = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static async Task SaveAsync(IEnumerable<BrowserTab> tabs, int activeIndex)
    {
        await SaveGate.WaitAsync();
        try
        {
            var snapshots = tabs.Select(t => new TabSnapshot
            {
                Address       = t.Address,
                Title         = t.Title,
                IsAudioOnly   = t.IsAudioOnly,
                IsDarkMode    = t.IsDarkMode,
                DarkBrightness = t.DarkBrightness,
                DarkContrast   = t.DarkContrast,
                DarkSaturation = t.DarkSaturation,
                ScrollY       = t.ScrollY,
                MediaPosition = t.MediaPosition,
                WasPlaying    = t.WasPlaying,
                CustomTitle   = t.CustomTitle,
                ColorHex      = t.ColorHex,
            }).ToList();

            if (snapshots.Count == 0) return;

            var session = new Session
            {
                Tabs           = snapshots,
                ActiveTabIndex = Math.Clamp(activeIndex, 0, snapshots.Count - 1),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            var json = JsonSerializer.Serialize(session, JsonOptions);
            // Write to a temp file first, then replace atomically — avoids
            // truncating the real file if the process is killed mid-write.
            var tmp = SessionPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, SessionPath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStore] Save failed: {ex.Message}");
        }
        finally
        {
            SaveGate.Release();
        }
    }

    public static async Task<Session?> LoadAsync()
    {
        if (!File.Exists(SessionPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(SessionPath);
            return JsonSerializer.Deserialize<Session>(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStore] Load failed: {ex.Message}");
            return null;
        }
    }
}
