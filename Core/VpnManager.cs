using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace FoxBrowser.Core;

public enum VpnConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public sealed class VpnProfile
{
    public VpnProfile(string path)
    {
        FilePath = path;
        Name = Path.GetFileNameWithoutExtension(path);
    }

    public string Name { get; }
    public string FilePath { get; }
}

public sealed class VpnManager
{
    private readonly string _profileDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FoxBrowser", "VPN");
    private string? _activeTunnel;

    public VpnManager()
    {
        Directory.CreateDirectory(_profileDirectory);
        Profiles = new ObservableCollection<VpnProfile>();
        RefreshProfiles();
    }

    public ObservableCollection<VpnProfile> Profiles { get; }
    public VpnConnectionState State { get; private set; } = VpnConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public VpnProfile? ActiveProfile { get; private set; }
    public bool IsConnected => State == VpnConnectionState.Connected;
    public string? WireGuardPath => FindWireGuard();
    public event EventHandler? StateChanged;

    public void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var path in Directory.EnumerateFiles(_profileDirectory, "*.conf"))
            Profiles.Add(new VpnProfile(path));
    }

    public VpnProfile ImportTunnelConfig(string confFilePath)
    {
        if (!File.Exists(confFilePath)) throw new FileNotFoundException("Конфігурацію WireGuard не знайдено.", confFilePath);
        if (!string.Equals(Path.GetExtension(confFilePath), ".conf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Потрібен файл конфігурації з розширенням .conf.");

        var safeName = Path.GetFileNameWithoutExtension(confFilePath);
        foreach (var invalid in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(invalid, '_');
        var destination = Path.Combine(_profileDirectory, safeName + ".conf");
        File.Copy(confFilePath, destination, true);
        RefreshProfiles();
        return Profiles.First(x => string.Equals(x.FilePath, destination, StringComparison.OrdinalIgnoreCase));
    }

    public async Task ConnectAsync(VpnProfile profile, CancellationToken cancellationToken = default)
    {
        if (State == VpnConnectionState.Connecting) return;
        var wireGuard = FindWireGuard();
        if (wireGuard is null)
        {
            State = VpnConnectionState.Error;
            LastError = "WireGuard for Windows не встановлено. Завантажте його з https://www.wireguard.com/install/";
            StateChanged?.Invoke(this, EventArgs.Empty);
            throw new InvalidOperationException(LastError);
        }

        State = VpnConnectionState.Connecting;
        LastError = null;
        ActiveProfile = profile;
        _activeTunnel = profile.Name;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var result = await RunAsync(wireGuard, cancellationToken, "/installtunnelservice", profile.FilePath);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
            State = VpnConnectionState.Connected;
        }
        catch (Exception ex)
        {
            State = VpnConnectionState.Error;
            LastError = ex.Message;
            throw;
        }
        finally { StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_activeTunnel)) return;
        var wireGuard = FindWireGuard();
        if (wireGuard is null) throw new InvalidOperationException("WireGuard for Windows не встановлено.");
        var result = await RunAsync(wireGuard, cancellationToken, "/uninstalltunnelservice", _activeTunnel);
        if (result.ExitCode != 0)
        {
            State = VpnConnectionState.Error;
            LastError = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            StateChanged?.Invoke(this, EventArgs.Empty);
            throw new InvalidOperationException(LastError);
        }
        _activeTunnel = null;
        ActiveProfile = null;
        State = VpnConnectionState.Disconnected;
        LastError = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string executable, CancellationToken cancellationToken, params object[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(Convert.ToString(argument) ?? string.Empty);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string? FindWireGuard()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WireGuard", "wireguard.exe"),
        };
        foreach (var candidate in candidates) if (File.Exists(candidate)) return candidate;

        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\wireguard.exe");
        var registered = key?.GetValue(null) as string;
        if (!string.IsNullOrWhiteSpace(registered) && File.Exists(registered)) return registered;

        foreach (var segment in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(segment.Trim(), "wireguard.exe");
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* Ignore unreadable PATH entries. */ }
        }

        return null;
    }
}
