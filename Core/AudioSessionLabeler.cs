using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FoxBrowser.Core;

/// <summary>
/// Labels only audio sessions owned by FoxBrowser's descendant WebView2 processes.
/// It never changes sessions belonging to unrelated WebView2 applications.
/// </summary>
public sealed class AudioSessionLabeler : IDisposable
{
    private const uint SnapshotProcess = 0x00000002;
    private readonly uint _rootProcessId;
    private string _displayName;
    private string _iconPath;
    private readonly MMDeviceEnumerator _devices = new();
    private readonly AudioSessionManager _sessions;
    private readonly System.Threading.Timer _refreshTimer;
    private readonly object _gate = new();
    private bool _disposed;

    public AudioSessionLabeler(uint rootProcessId, string iconPath, string displayName = "FoxBrowser")
    {
        _rootProcessId = rootProcessId;
        _iconPath = iconPath;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? "FoxBrowser" : displayName;
        var device = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _sessions = device.AudioSessionManager;
        _sessions.OnSessionCreated += OnSessionCreated;
        _refreshTimer = new System.Threading.Timer(_ => RefreshAndLabel(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public void UpdateIdentity(string displayName, string iconPath)
    {
        _displayName = string.IsNullOrWhiteSpace(displayName) ? "FoxBrowser" : displayName;
        _iconPath = iconPath;
        RefreshAndLabel();
    }

    private void OnSessionCreated(object sender, IAudioSessionControl nativeSession)
    {
        LabelSession(new AudioSessionControl(nativeSession), disposeAfter: true);
    }

    private void RefreshAndLabel()
    {
        if (_disposed) return;
        try
        {
            var ownedPids = GetFoxBrowserWebViewPids();
            var sessionCollection = _sessions.Sessions;
            if (sessionCollection is null) return;
            for (var index = 0; index < sessionCollection.Count; index++)
            {
                var session = sessionCollection[index];
                if (session is null) continue;
                try
                {
                    if (ownedPids.Contains(session.GetProcessID))
                        LabelSession(session, ownedPids, disposeAfter: false);
                }
                finally { session.Dispose(); }
            }
        }
        catch { /* Audio devices can disappear or be replaced while Windows is running. */ }
    }

    private void LabelSession(AudioSessionControl session, HashSet<uint>? knownOwnedPids = null, bool disposeAfter = false)
    {
        try
        {
            var ownedPids = knownOwnedPids ?? GetFoxBrowserWebViewPids();
            if (!ownedPids.Contains(session.GetProcessID)) return;
            session.DisplayName = _displayName;
            if (File.Exists(_iconPath)) session.IconPath = _iconPath;
        }
        catch { /* A session may close between PID lookup and property update. */ }
        finally { if (disposeAfter) session.Dispose(); }
    }

    private HashSet<uint> GetFoxBrowserWebViewPids()
    {
        var processes = ReadProcesses();
        var children = new Dictionary<uint, List<ProcessRecord>>();
        foreach (var process in processes)
        {
            if (!children.TryGetValue(process.ParentId, out var list))
                children[process.ParentId] = list = new List<ProcessRecord>();
            list.Add(process);
        }

        var result = new HashSet<uint>();
        var pending = new Queue<uint>(new[] { _rootProcessId });
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            if (!children.TryGetValue(parent, out var descendants)) continue;
            foreach (var child in descendants)
            {
                pending.Enqueue(child.ProcessId);
                if (child.Name.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
                    result.Add(child.ProcessId);
            }
        }
        return result;
    }

    private static List<ProcessRecord> ReadProcesses()
    {
        var result = new List<ProcessRecord>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                result.Add(new ProcessRecord(entry.ProcessId, entry.ParentProcessId, entry.ExeFile));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _refreshTimer.Dispose();
            _sessions.OnSessionCreated -= OnSessionCreated;
            _sessions.Dispose();
            _devices.Dispose();
        }
    }

    private readonly record struct ProcessRecord(uint ProcessId, uint ParentId, string Name);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
