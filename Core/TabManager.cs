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
    private bool _restoringSession;
    private readonly string _blockedElementsPath = Path.Combine(AppContext.BaseDirectory, "blocked-elements.json");
    private readonly Dictionary<string, List<string>> _blockedElements;

    public TabManager(Grid host, CoreWebView2Environment environment, bool extensionsAvailable = true)
    {
        _host = host;
        _environment = environment;
        ExtensionsAvailable = extensionsAvailable;
        _view = new WebView2 { Visibility = Visibility.Visible };
        _host.Children.Add(_view);
        _blockedElements = LoadBlockedElements();
    }

    public ObservableCollection<BrowserTab> Tabs { get; } = new();
    public BrowserTab? ActiveTab { get; private set; }
    public bool IsMinimizedMode => _minimizedMode;
    public bool ExtensionsAvailable { get; private set; }
    public bool SessionReady { get; private set; }
    public bool IsElementPickerActive { get; private set; }
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
        _view.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        _restoringSession = true;
        try
        {
            await RestoreSessionOrDefaultAsync();
        }
        finally
        {
            _restoringSession = false;
            SessionReady = true;
        }
        await SaveSessionAsync();
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
                IsDarkMode = snap.IsDarkMode,
                DarkBrightness = snap.DarkBrightness,
                DarkContrast = snap.DarkContrast,
                DarkSaturation = snap.DarkSaturation,
                ScrollY = snap.ScrollY,
                MediaPosition = snap.MediaPosition,
                WasPlaying = snap.WasPlaying,
                CustomTitle = snap.CustomTitle,
                ColorHex = snap.ColorHex,
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

    public async Task<BrowserTab?> DuplicateTab(BrowserTab source)
    {
        var index = Tabs.IndexOf(source);
        if (index < 0) return null;
        var copy = new BrowserTab(source.Address)
        {
            Title = source.Title,
            CustomTitle = source.CustomTitle,
            ColorHex = source.ColorHex,
        };
        Tabs.Insert(index + 1, copy);
        await SaveSessionAsync();
        return copy;
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
            IsElementPickerActive = false;
            ActiveTab = tab;
            _audioOnly = tab.IsAudioOnly;
            ActiveTabStateChanged?.Invoke(this, EventArgs.Empty);
            _navigationInProgress = true;
            await UpdateDocumentCreatedScriptAsync();
            core.Navigate(tab.Address);
            if (!_restoringSession) await SaveSessionAsync();
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
        await UpdateDocumentCreatedScriptAsync();
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

    public async Task ToggleDarkModeAsync()
    {
        if (ActiveTab is null) return;
        ActiveTab.IsDarkMode = !ActiveTab.IsDarkMode;
        await ApplyDarkModeAsync(ActiveTab.IsDarkMode);
        await SaveSessionAsync();
    }

    public async Task SetDarkModeParametersAsync(double brightness, double contrast, double saturation)
    {
        if (ActiveTab is null) return;
        ActiveTab.DarkBrightness = Math.Clamp(brightness, 0.45, 1.0);
        ActiveTab.DarkContrast = Math.Clamp(contrast, 0.8, 1.4);
        ActiveTab.DarkSaturation = Math.Clamp(saturation, 0.0, 1.2);
        if (ActiveTab.IsDarkMode) await ApplyDarkModeAsync(true);
        await SaveSessionAsync();
    }

    public async Task StartElementPickerAsync()
    {
        if (_view.CoreWebView2 is null || ActiveTab is null) return;
        if (IsElementPickerActive)
        {
            await _view.CoreWebView2.ExecuteScriptAsync("window.__foxElementPicker?.();");
            IsElementPickerActive = false;
            ActiveTabStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        IsElementPickerActive = true;
        ActiveTabStateChanged?.Invoke(this, EventArgs.Empty);
        await _view.CoreWebView2.ExecuteScriptAsync("""
          (()=>{
            if(window.__foxElementPicker){window.__foxElementPicker();return;}
            const previousCursor=document.documentElement.style.cursor;
            const getSelector=(el)=>{
              if(el.id)return '#'+CSS.escape(el.id);
              const parts=[];
              while(el&&el.nodeType===1&&el!==document.body){
                let part=el.tagName.toLowerCase();
                if(el.classList.length)part+='.'+[...el.classList].slice(0,2).map(CSS.escape).join('.');
                const siblings=[...el.parentElement.children].filter(x=>x.tagName===el.tagName);
                if(siblings.length>1)part+=`:nth-of-type(${siblings.indexOf(el)+1})`;
                parts.unshift(part);el=el.parentElement;
              }
              return parts.join(' > ');
            };
            let last=null;
            const over=(event)=>{event.stopPropagation();if(last)last.style.outline='';last=event.target;last.style.outline='2px solid #78AFFF';};
            const click=(event)=>{event.preventDefault();event.stopPropagation();const selector=getSelector(event.target);cleanup();window.chrome?.webview?.postMessage(JSON.stringify({type:'fox-block-element',selector}));};
            const cleanup=()=>{document.removeEventListener('mouseover',over,true);document.removeEventListener('click',click,true);if(last)last.style.outline='';document.documentElement.style.cursor=previousCursor;window.__foxElementPicker=null;};
            window.__foxElementPicker=cleanup;document.documentElement.style.cursor='crosshair';document.addEventListener('mouseover',over,true);document.addEventListener('click',click,true);
          })();
          """);
    }

    public string? CurrentHost => GetCurrentHost();

    public IReadOnlyList<string> GetBlockedSelectors()
    {
        var host = GetCurrentHost();
        return host is not null && _blockedElements.TryGetValue(host, out var selectors)
            ? selectors.ToArray()
            : Array.Empty<string>();
    }

    public async Task RemoveBlockedSelectorAsync(string selector)
    {
        var host = GetCurrentHost();
        if (host is null || !_blockedElements.TryGetValue(host, out var selectors)) return;
        selectors.RemoveAll(x => string.Equals(x, selector, StringComparison.Ordinal));
        SaveBlockedElements();
        await ApplyBlockedElementsAsync();
    }

    public async Task ClearBlockedForCurrentHostAsync()
    {
        var host = GetCurrentHost();
        if (host is null) return;
        _blockedElements.Remove(host);
        SaveBlockedElements();
        await ApplyBlockedElementsAsync();
    }

    public async Task ClearAllBlockedAsync()
    {
        _blockedElements.Clear();
        SaveBlockedElements();
        await ApplyBlockedElementsAsync();
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

    public async Task CloseTabAsync(BrowserTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.Remove(tab);
        if (Tabs.Count == 0)
        {
            await AddTabAsync();
            return;
        }
        if (ActiveTab == tab)
            await SwitchToTabAsync(Tabs[Math.Min(index, Tabs.Count - 1)]);
        else
            await SaveSessionAsync();
    }

    public async Task MoveTabAsync(BrowserTab tab, int targetIndex)
    {
        var sourceIndex = Tabs.IndexOf(tab);
        if (sourceIndex < 0 || Tabs.Count < 2) return;
        targetIndex = Math.Clamp(targetIndex, 0, Tabs.Count - 1);
        if (sourceIndex == targetIndex) return;
        Tabs.Move(sourceIndex, targetIndex);
        await SaveSessionAsync();
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
        await ApplyDarkModeAsync(ActiveTab.IsDarkMode);
        await ApplyBlockedElementsAsync();
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

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<string>(e.WebMessageAsJson);
            if (string.IsNullOrWhiteSpace(payload)) return;
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("type", out var type) || type.GetString() != "fox-block-element") return;
            var selector = document.RootElement.GetProperty("selector").GetString();
            var host = GetCurrentHost();
            if (string.IsNullOrWhiteSpace(selector) || string.IsNullOrWhiteSpace(host)) return;
            if (!_blockedElements.TryGetValue(host, out var selectors))
                _blockedElements[host] = selectors = new List<string>();
            if (!selectors.Contains(selector, StringComparer.Ordinal)) selectors.Add(selector);
            IsElementPickerActive = false;
            SaveBlockedElements();
            ActiveTabStateChanged?.Invoke(this, EventArgs.Empty);
            _ = ApplyBlockedElementsAsync();
        }
        catch (JsonException) { }
    }

    private async Task ApplyBlockedElementsAsync()
    {
        if (_view.CoreWebView2 is null) return;
        var host = GetCurrentHost();
        var selectors = host is not null && _blockedElements.TryGetValue(host, out var savedSelectors)
            ? savedSelectors
            : new List<string>();
        var json = JsonSerializer.Serialize(selectors);
        var script = """
          (()=>{const selectors=__SELECTORS__;let s=document.getElementById('fox-blocked-elements');if(!selectors.length){s?.remove();return;}if(!s){s=document.createElement('style');s.id='fox-blocked-elements';(document.head||document.documentElement).appendChild(s);}s.textContent=selectors.map(x=>x+'{display:none!important;}').join('');})();
          """.Replace("__SELECTORS__", json, StringComparison.Ordinal);
        try { await _view.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }

    private async Task UpdateDocumentCreatedScriptAsync()
    {
        var core = _view.CoreWebView2;
        if (core is null) return;

        var host = GetCurrentHost();
        var selectors = host is not null && _blockedElements.TryGetValue(host, out var savedSelectors)
            ? savedSelectors
            : new List<string>();
        var darkEnabled = ActiveTab?.IsDarkMode == true &&
                          Uri.TryCreate(ActiveTab.Address, UriKind.Absolute, out var address) &&
                          (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);
        var brightness = (ActiveTab?.DarkBrightness ?? 0.72).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var contrast = (ActiveTab?.DarkContrast ?? 1.08).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var saturation = (ActiveTab?.DarkSaturation ?? 0.88).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var script = """
          (()=>{
            const selectors=__SELECTORS__, dark=__DARK__, brightness=__BRIGHTNESS__, contrast=__CONTRAST__, saturation=__SATURATION__;
            const apply=()=>{
              const root=document.documentElement;
              if(!root){setTimeout(apply,0);return;}
              let darkStyle=document.getElementById('fox-dark-mode');
              if(!darkStyle){darkStyle=document.createElement('style');darkStyle.id='fox-dark-mode';root.appendChild(darkStyle);}
              let blockedStyle=document.getElementById('fox-blocked-elements');
              if(!blockedStyle){blockedStyle=document.createElement('style');blockedStyle.id='fox-blocked-elements';root.appendChild(blockedStyle);}
              const darkCss=dark?'html{background:#111!important;color-scheme:dark!important}html.fox-dark-mode{background:#111!important;color-scheme:dark!important}html.fox-dark-mode body{background-color:#15171b!important;filter:brightness('+brightness+') contrast('+contrast+') saturate('+saturation+')!important}html.fox-dark-mode input,html.fox-dark-mode textarea,html.fox-dark-mode select,html.fox-dark-mode button{color-scheme:dark!important}':'';
              darkStyle.textContent=darkCss;
              blockedStyle.textContent=selectors.map(x=>x+'{display:none!important;}').join('');
              if(dark) root.classList.add('fox-dark-mode'); else root.classList.remove('fox-dark-mode');
            };
            apply();
          })();
          """
            .Replace("__SELECTORS__", JsonSerializer.Serialize(selectors), StringComparison.Ordinal)
            .Replace("__DARK__", darkEnabled ? "true" : "false", StringComparison.Ordinal)
            .Replace("__BRIGHTNESS__", brightness, StringComparison.Ordinal)
            .Replace("__CONTRAST__", contrast, StringComparison.Ordinal)
            .Replace("__SATURATION__", saturation, StringComparison.Ordinal);
        try { await core.AddScriptToExecuteOnDocumentCreatedAsync(script); } catch { }
    }

    private string? GetCurrentHost()
    {
        return Uri.TryCreate(ActiveTab?.Address, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.Host.ToLowerInvariant()
            : null;
    }

    private Dictionary<string, List<string>> LoadBlockedElements()
    {
        try
        {
            if (!File.Exists(_blockedElementsPath)) return new(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_blockedElementsPath))
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveBlockedElements()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_blockedElementsPath)!);
            File.WriteAllText(_blockedElementsPath, JsonSerializer.Serialize(_blockedElements, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
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

    private async Task ApplyDarkModeAsync(bool enabled)
    {
        if (_view.CoreWebView2 is null || ActiveTab is null) return;
        if (!Uri.TryCreate(ActiveTab.Address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;

        var brightness = ActiveTab.DarkBrightness.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var contrast = ActiveTab.DarkContrast.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var saturation = ActiveTab.DarkSaturation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var script = enabled
            ? """
              (()=>{
                const root=document.documentElement;
                if(!root)return;
                root.classList.add('fox-dark-mode');
                let s=document.getElementById('fox-dark-mode');
                if(!s){s=document.createElement('style');s.id='fox-dark-mode';(document.head||root).appendChild(s);}
                s.textContent=`html.fox-dark-mode{background:#111!important;color-scheme:dark!important}html.fox-dark-mode body{background-color:#15171b!important;filter:brightness(__BRIGHTNESS__) contrast(__CONTRAST__) saturate(__SATURATION__)!important}html.fox-dark-mode input,html.fox-dark-mode textarea,html.fox-dark-mode select,html.fox-dark-mode button{color-scheme:dark!important}`;
              })();
              """
            : """
              (()=>{document.documentElement?.classList.remove('fox-dark-mode');document.getElementById('fox-dark-mode')?.remove();})();
              """;
        script = script.Replace("__BRIGHTNESS__", brightness, StringComparison.Ordinal)
                     .Replace("__CONTRAST__", contrast, StringComparison.Ordinal)
                     .Replace("__SATURATION__", saturation, StringComparison.Ordinal);
        try { await _view.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }

    private static string NormalizeAddress(string value)
    {
        var text = value.Trim();
        if (string.IsNullOrEmpty(text)) return "https://www.google.com";
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return text;
        return $"https://www.google.com/search?q={Uri.EscapeDataString(text)}";
    }
}
