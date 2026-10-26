namespace FoxBrowser.Core;

public static class StartupTrace
{
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static readonly string LogPath = System.IO.Path.Combine(AppContext.BaseDirectory, "startup-trace.log");
    private static readonly bool ToFile = System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "startup-trace.on"));

    public static void Mark(string label)
    {
        var line = $"[Startup] {Clock.ElapsedMilliseconds,6} ms  {label}";
        System.Diagnostics.Debug.WriteLine(line);
        if (!ToFile) return;
        try { System.IO.File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }
}
