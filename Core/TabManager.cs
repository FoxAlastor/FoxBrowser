using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FoxBrowser.Core;

public sealed class TabManager : IAsyncDisposable
{
    private static readonly string[] BlockedHosts =
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com",
        "googletagmanager.com", "google-analytics.com", "analytics.google.com", "facebook.net",
        "connect.facebook.net", "hotjar.com", "segment.io", "amplitude.com", "scorecardresearch.com",
        "adsrvr.org", "adnxs.com", "taboola.com", "outbrain.com"
    };

    private readonly Grid _host;
    private readonly CoreWebView2Environment _environment;
    private readonly WebView2 _view;
    private bool _audioOnly;
    private bool _minimizedMode;
    private bool _navigationInProgress;
    private bool _switchInProgress;

    public TabManager(Grid host, CoreWebView2Environment environment, bool extensionsAvailable = true)
    {
        _host = host;
        _environment = environment;
        ExtensionsAvailable = extensionsAvailable;
        _view = new WebView2 { Visibility = Visibility.Visible };
        _host.Children.Add(_view);
    }

    public ObservableCollection<BrowserTab> Tabs { get; } = new();
    public BrowserTab? ActiveTab { get; private set; }
    public bool IsMinimizedMode => _minimizedMode;
    public bool ExtensionsAvailable { get; private set; }
    public CoreWebView2Environment Environment => _environment;
    public WebView2 View => _view;
    public ExtensionManager? ExtensionManager { get; private set; }
    public event EventHandler? ActiveTabStateChanged;

    public async Task InitializeAsync()
    {
        await _view.EnsureCoreWebView2Async(_environment);
        try
        {
            ExtensionManager = new ExtensionManager(_view.CoreWebView2.Profile);
            await ExtensionManager.RefreshAsync();
        }
        catch
        {
            ExtensionManager = null;
            ExtensionsAvailable = false;
        }
        ConfigureResourceFirewall(_view.CoreWebView2);
        _view.CoreWebView2.NavigationStarting += OnNavigationStarting;
        _view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        _view.CoreWebView2.DocumentTitleChanged += OnDocumentTitleChanged;
        _view.CoreWebView2.IsDocumentPlayingAudioChanged += OnAudioStateChanged;
        _view.CoreWebView2.HistoryChanged += OnHistoryChanged;
        await RestoreSessionOrDefaultAsync();
    }

    public async Task SaveSessionAsync()
    {
        var activeIndex = ActiveTab is not null ? Tabs.IndexOf(ActiveTab) : 0;
        await SessionStore.SaveAsync(Tabs, activeIndex);
    }

    private async Task RestoreSessionOrDefaultAsync()
    {
        var session = await SessionStore.LoadAsync();
        if (session is null || session.Tabs.Count == 0)
        {
            await AddTabAsync();
            return;
        }

        BrowserTab? activeTab = null;
        for (int i = 0; i < session.Tabs.Count; i++)
        {
            var snap = session.Tabs[i];
            var tab = new BrowserTab(snap.Address)
            {
                Title = snap.Title,
                IsAudioOnly = snap.IsAudioOnly,
                ScrollY = snap.ScrollY,
                MediaPosition = snap.MediaPosition,
                WasPlaying = snap.WasPlaying,
            };
            Tabs.Add(tab);
            if (i == session.ActiveTabIndex) activeTab = tab;
        }
        await SwitchToTabAsync(activeTab ?? Tabs[0]);
    }

    public async Task<BrowserTab> AddTabAsync(string address = "https://www.google.com")
    {
        var tab = new BrowserTab(NormalizeAddress(address));
        Tabs.Add(tab);
        await SwitchToTabAsync(tab);
        return tab;
    }

    public async Task SwitchToTabAsync(BrowserTab? tab)
    {
        if (tab is null || !Tabs.Contains(tab)) return;
        var core = _view.CoreWebView2;
        if (core is null || ReferenceEquals(ActiveTab, tab) || _switchInProgress) return;
        _switchInProgress = true;
        try
        {
            if (ActiveTab is not null) await CaptureActiveStateAsync();
            ActiveTab = tab;
            _audioOnly = tab.IsAudioOnly;
            ActiveTabStateChanged?.Invoke(this, EventArgs.Empty);
            _navigationInProgress = true;
            core.Navigate(tab.Address);
            await Task.Yield();
        }
        finally { _switchInProgress = false; }
    }

    public async Task NavigateAsync(BrowserTab tab, string address)
    {
        if (!Tabs.Contains(tab)) return;
        var core = _view.CoreWebView2;
        if (core is null) return;
        tab.Address = NormalizeAddress(address);
        if (ActiveTab != tab) { await SwitchToTabAsync(tab); return; }
        _navigationInProgress = true;
        core.Navigate(tab.Address);
    }

    public Task OpenExtensionOptionsAsync(string extensionId)
    {
        var core = _view.CoreWebView2;
        if (core is null || string.IsNullOrWhiteSpace(extensionId)) return Task.CompletedTask;
        core.Navigate($"chrome-extension://{extensionId}/options.html");
        return Task.CompletedTask;
    }

    public async Task OpenUrlAsync(string address)
    {
        if (ActiveTab is null) return;
        if (address.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
        {
            var core = _view.CoreWebView2;
            if (core is null) return;
            ActiveTab.Address = address;
            core.Navigate(address);
            return;
        }
        await NavigateAsync(ActiveTab, address);
    }

    public async Task ToggleAudioOnlyAsync()
    {
        if (ActiveTab is null) return;
        _audioOnly = !_audioOnly;
        ActiveTab.IsAudioOnly = _audioOnly;
        await ApplyLightweightPageModeAsync(_audioOnly || _minimizedMode);
    }

    public async Task SetMinimizedModeAsync(bool enabled)
    {
        _minimizedMode = enabled;
        await ApplyLightweightPageModeAsync(enabled || _audioOnly);
    }

    public async Task SoftReloadIfNeededAsync(long thresholdBytes = 850L * 1024 * 1024)
    {
        using var process = Process.GetCurrentProcess();
        if (process.WorkingSet64 < thresholdBytes || ActiveTab is null || _navigationInProgress) return;
        await CaptureActiveStateAsync();
        _navigationInProgress = true;
        _view.CoreWebView2?.Navigate(ActiveTab.Address);
    }

    public void GoBack() { var core = _view.CoreWebView2; if (core?.CanGoBack == true) core.GoBack(); }
    public void GoForward() { var core = _view.CoreWebView2; if (core?.CanGoForward == true) core.GoForward(); }
    public void Reload() { var core = _view.CoreWebView2; if (core is null) return; _navigationInProgress = true; core.Reload(); }

    public void CloseTab(BrowserTab tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (Tabs.Count == 0) { _ = AddTabAsync(); return; }
        if (ActiveTab == tab) _ = SwitchToTabAsync(Tabs[Math.Min(index, Tabs.Count - 1)]);
        _ = SaveSessionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (ActiveTab is not null) await CaptureActiveStateAsync();
        await SaveSessionAsync();
        _view.Dispose();
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_minimizedMode && !_navigationInProgress) e.Cancel = true;
        if (ActiveTab is not null) ActiveTab.IsLoading = true;
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _navigationInProgress = false;
        if (ActiveTab is null) return;
        ActiveTab.IsLoading = false;
        ActiveTab.Address = _view.Source?.ToString() ?? ActiveTab.Address;
        ActiveTab.Title = string.IsNullOrWhiteSpace(_view.CoreWebView2.DocumentTitle) ? "Нова вкладка" : _view.CoreWebView2.DocumentTitle;
        ActiveTabStateChanged?.Invoke(this, EventArgs.Empty);
        await ApplyLightweightPageModeAsync(_audioOnly || _minimizedMode);
        await RestoreActiveStateAsync();
        await SaveSessionAsync();
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        if (ActiveTab is not null) ActiveTab.Title = string.IsNullOrWhiteSpace(_view.CoreWebView2.DocumentTitle) ? "Нова вкладка" : _view.CoreWebView2.DocumentTitle;
    }

    private void OnAudioStateChanged(object? sender, object e)
    {
        if (ActiveTab is not null) ActiveTab.IsPlayingAudio = _view.CoreWebView2.IsDocumentPlayingAudio;
    }

    private void OnHistoryChanged(object? sender, object e)
    {
        if (ActiveTab is null) return;
        var current = _view.CoreWebView2.Source;
        if (string.IsNullOrEmpty(current) || current == ActiveTab.Address) return;
        ActiveTab.Address = current;
        ActiveTabStateChanged?.Invoke(this, EventArgs.Empty);
        _ = SaveSessionAsync();
    }

    private async Task CaptureActiveStateAsync()
    {
        if (ActiveTab is null || _view.CoreWebView2 is null) return;
        try
        {
            var json = await _view.CoreWebView2.ExecuteScriptAsync("JSON.stringify({scrollY:window.scrollY, media:[...document.querySelectorAll('video,audio')].map(x=>({time:x.currentTime||0,playing:!x.paused}))})");
            var payload = JsonSerializer.Deserialize<string>(json);
            if (string.IsNullOrWhiteSpace(payload)) return;
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("scrollY", out var scroll)) ActiveTab.ScrollY = scroll.GetDouble();
            if (doc.RootElement.TryGetProperty("media", out var media) && media.GetArrayLength() > 0)
            {
                var first = media[0];
                if (first.TryGetProperty("time", out var time)) ActiveTab.MediaPosition = time.GetDouble();
                if (first.TryGetProperty("playing", out var playing)) ActiveTab.WasPlaying = playing.GetBoolean();
            }
        }
        catch (JsonException) { }
    }

    private async Task RestoreActiveStateAsync()
    {
        if (ActiveTab is null || _view.CoreWebView2 is null) return;
        var scroll = ActiveTab.ScrollY.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var position = ActiveTab.MediaPosition.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var resume = ActiveTab.WasPlaying ? ".play().catch(()=>{});" : "";
        var script = $"(() => {{ const restore=()=>{{ window.scrollTo(0,{scroll}); document.querySelectorAll('video,audio').forEach(x=>{{ try {{ x.currentTime={position}; {resume} }} catch {{}} }}); }}; restore(); setTimeout(restore,300); setTimeout(restore,1000); }})();";
        await _view.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void ConfigureResourceFirewall(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = e.Request.Uri;
        var host = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : string.Empty;
        var blockedHost = BlockedHosts.Any(x => host.Equals(x, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + x, StringComparison.OrdinalIgnoreCase));
        var visualWaste = _audioOnly || _minimizedMode;
        var blockedContext = visualWaste && (e.ResourceContext is CoreWebView2WebResourceContext.Image or CoreWebView2WebResourceContext.Font);
        var thirdPartyFrame = visualWaste && (uri.Contains("/embed/", StringComparison.OrdinalIgnoreCase) || host.Contains("livechat", StringComparison.OrdinalIgnoreCase));
        if (!blockedHost && !blockedContext && !thirdPartyFrame) return;
        e.Response = _view.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(Array.Empty<byte>()), 204, "No Content", "");
    }

    private async Task ApplyLightweightPageModeAsync(bool enabled)
    {
        if (_view.CoreWebView2 is null) return;
        var script = enabled
            ? """
              (()=>{let s=document.getElementById('fox-lightweight');if(!s){s=document.createElement('style');s.id='fox-lightweight';s.textContent='img,picture,svg,canvas{display:none!important}video{width:1px!important;height:1px!important;opacity:0!important;position:fixed!important;left:-10000px!important;top:-10000px!important}';document.documentElement.appendChild(s)};document.querySelectorAll('link[rel="preload"],link[rel="prefetch"],link[rel="next"]').forEach(x=>x.remove());})();
              """
            : "document.getElementById('fox-lightweight')?.remove();";
        await _view.CoreWebView2.ExecuteScriptAsync(script);
    }

    private static string NormalizeAddress(string value)
    {
        var text = value.Trim();
        if (string.IsNullOrEmpty(text)) return "https://www.google.com";
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return text;
        return $"https://www.google.com/search?q={Uri.EscapeDataString(text)}";
    }
}
