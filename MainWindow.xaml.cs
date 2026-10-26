using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using FoxBrowser.Core;
using FoxBrowser.Native;
using FoxBrowser.UI;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using WpfScaleTransform = System.Windows.Media.ScaleTransform;
using WpfTranslateTransform = System.Windows.Media.TranslateTransform;

namespace FoxBrowser;

public partial class MainWindow : Window
{
    private TabManager? _tabManager;
    private TrayIconManager? _tray;
    private bool _allowClose;
    private bool _closingAfterSessionSave;
    private readonly DispatcherTimer _watchdogTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private AudioSessionLabeler? _audioLabeler;
    private bool _updatingDarkModeMenu;
    private AppSettings _appSettings;
    private WpfPoint _tabDragStartPoint;
    private BrowserTab? _tabDragItem;
    private TabItem? _draggedTabContainer;
    private TabItem? _dragPreviewTarget;
    private bool _topPanelVisible = true;
    private bool _addressBarVisible = true;
    private int _extensionPopupCount;
    private readonly DispatcherTimer _addressBarTimer = new();
    private readonly DispatcherTimer _geometrySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _geometryReady;
    private bool _startupContentRendered;
    private Popup? _tabContextPopup;

    private const int WmNcRButtonDown = 0x00A4;
    private const int WmNcRButtonUp = 0x00A5;
    private const int HtSysMenu = 3;

    public MainWindow()
    {
        InitializeComponent();
        _appSettings = AppSettingsStore.Load();
        RestoreWindowGeometry(_appSettings);
        RestoreTopPanelState();
        RestoreAddressBarState();
        ApplyAppIdentity(_appSettings);
        _addressBarTimer.Tick += (_, _) => HideAddressBarIfAllowed();
        _geometrySaveTimer.Tick += (_, _) =>
        {
            _geometrySaveTimer.Stop();
            SaveWindowGeometry();
        };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SourceInitialized += MainWindow_SourceInitialized;
        ContentRendered += MainWindow_ContentRendered;
        Loaded += MainWindow_Loaded;
        _geometryReady = true;
        StartupTrace.Mark("MainWindow constructor completed");
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        StartupTrace.Mark("MainWindow_SourceInitialized");
        ApplyDarkTitleBar(true);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.SetWindowClassBackground(hwnd, 0x0F, 0x12, 0x17);
        if (source?.CompositionTarget is not null)
            source.CompositionTarget.BackgroundColor = System.Windows.Media.Color.FromRgb(0x0F, 0x12, 0x17);
        Win32Interop.SetWindowCloaked(hwnd, true);
        source?.AddHook(MainWindowWndProc);
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_startupContentRendered) return;
        _startupContentRendered = true;
        StartupTrace.Mark("MainWindow_ContentRendered");
    }

    private IntPtr MainWindowWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((msg == WmNcRButtonDown || msg == WmNcRButtonUp) && wParam.ToInt32() == HtSysMenu)
        {
            handled = true;
            if (msg == WmNcRButtonUp)
                Dispatcher.BeginInvoke(OpenAppIdentityMenu);
        }
        return IntPtr.Zero;
    }

    private void OpenAppIdentityMenu()
    {
        var popup = new Popup
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };
        var item = new System.Windows.Controls.Button
        {
            Content = "Змінити назву й іконку…",
            Style = (Style)FindResource("FoxMenuButton")
        };
        item.Click += (_, _) =>
        {
            popup.IsOpen = false;
            OpenAppSettings();
        };
        var showAddressItem = new System.Windows.Controls.Button
        {
            Content = "Показати адресну панель",
            Style = (Style)FindResource("FoxMenuButton")
        };
        showAddressItem.Click += (_, _) =>
        {
            popup.IsOpen = false;
            ShowAddressBarFromMenu();
        };
        var showTopPanelItem = new System.Windows.Controls.Button
        {
            Content = "Показати вкладки й адресну панель",
            Style = (Style)FindResource("FoxMenuButton"),
            Visibility = _topPanelVisible ? Visibility.Collapsed : Visibility.Visible
        };
        showTopPanelItem.Click += (_, _) =>
        {
            popup.IsOpen = false;
            SetTopPanelVisible(true);
        };
        var shortcutsItem = new System.Windows.Controls.Button
        {
            Content = "Клавіатурні скорочення…",
            Style = (Style)FindResource("FoxMenuButton")
        };
        shortcutsItem.Click += (_, _) =>
        {
            popup.IsOpen = false;
            Shortcuts_Click(this, new RoutedEventArgs());
        };
        popup.Child = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 27, 34)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 64, 78)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = new StackPanel
            {
                Children = { item, shortcutsItem, showAddressItem, showTopPanelItem }
            }
        };
        popup.IsOpen = true;
    }

    private void OpenAppSettings()
    {
        var window = new AppSettingsWindow(_appSettings, ApplyAppIdentity) { Owner = this };
        window.ShowDialog();
    }

    private void ApplyAppIdentity(AppSettings settings)
    {
        _appSettings = settings;
        var iconPath = ResolveAppIconPath(settings);
        Title = string.IsNullOrWhiteSpace(settings.AppDisplayName) ? "FoxBrowser" : settings.AppDisplayName;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(iconPath, UriKind.Absolute);
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            Icon = image;
        }
        catch { }
        _tray?.UpdateIdentity(Title, iconPath);
        _audioLabeler?.UpdateIdentity(Title, iconPath);
    }

    private static string ResolveAppIconPath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AppIconPath) && File.Exists(settings.AppIconPath))
            return settings.AppIconPath;
        return Path.Combine(AppContext.BaseDirectory, "FoxBrowser.ico");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartupTrace.Mark("MainWindow_Loaded started");
        RestoreTopPanelState();
        try
        {
            _tray = new TrayIconManager(this, ExitApplication, ShowAddressBarFromMenu);
            _tray.UpdateIdentity(Title, ResolveAppIconPath(_appSettings));
            var environmentFactory = new WebView2EnvironmentFactory();
            var recoveryMode = false;
            CoreWebView2Environment environment;
            try
            {
                environment = await App.EnvironmentTask;
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x8007139Fu)
            {
                // Another FoxBrowser/WebView2 process may hold the persistent profile lock.
                // Use a disposable recovery profile so the browser can still start.
                recoveryMode = true;
                var recoveryData = Path.Combine(
                    AppContext.BaseDirectory, $"Profile_Recovery_{Environment.ProcessId}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(recoveryData);
                environment = await environmentFactory.CreateAsync(recoveryData);
            }
            StartupTrace.Mark("CoreWebView2Environment received");
            var startupNotice = environmentFactory.TakeStartupNotice();
            if (startupNotice is not null)
                System.Windows.MessageBox.Show(this, startupNotice, "FoxBrowser", MessageBoxButton.OK, MessageBoxImage.Information);
            _tabManager = new TabManager(WebViewHost, environment, !recoveryMode);
            _tabManager.ActiveTabStateChanged += (_, _) => Dispatcher.Invoke(() =>
            {
                RefreshAddressBar();
                RefreshModeIndicators();
            });
            _tabManager.ActiveDownloadsChanged += count => Dispatcher.Invoke(() =>
            {
                DownloadsButton.Content = count > 0 ? "⬇•" : "⬇";
            });
            DataContext = _tabManager;
            await _tabManager.InitializeAsync();
            TabStrip.SelectedItem = _tabManager.ActiveTab;
            RefreshModeIndicators();
            var iconPath = ResolveAppIconPath(_appSettings);
            try
            {
                _audioLabeler = new AudioSessionLabeler((uint)Process.GetCurrentProcess().Id, iconPath, Title);
            }
            catch
            {
                // Audio labeling is optional; a missing endpoint must not break browsing.
                _audioLabeler = null;
            }
            PinnedExtensionStrip.ItemsSource = _tabManager.ExtensionManager?.PinnedExtensions;
            _watchdogTimer.Tick += async (_, _) => await _tabManager.SoftReloadIfNeededAsync();
            _watchdogTimer.Start();
            BrowserStatus.Visibility = Visibility.Collapsed;
            RefreshAddressBar();
            ApplyAddressBarSettings(_appSettings);
            RevealWindowAfterStartup();
        }
        catch (Exception ex)
        {
            BrowserStatus.Text = $"WebView2 не запустився: {ex.Message}";
            BrowserStatus.Visibility = Visibility.Visible;
            RevealWindowAfterStartup();
        }
    }

    private void RevealWindowAfterStartup()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            Win32Interop.SetWindowCloaked(hwnd, false);
        }));
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e)
    {
        if (_tabManager is not null) await _tabManager.AddTabAsync();
        RefreshAddressBar();
    }

    private async void AddressBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await NavigateFromAddressBarAsync();
        Keyboard.ClearFocus();
    }

    private void AddressBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox)
            textBox.SelectAll();
    }

    private void AddressBox_PreviewMouseLeftButtonDown(object sender, WpfMouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox || textBox.IsKeyboardFocusWithin) return;
        e.Handled = true;
        textBox.Focus();
        textBox.SelectAll();
    }

    private async void Go_Click(object sender, RoutedEventArgs e) => await NavigateFromAddressBarAsync();

    private async void AudioOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_tabManager is null) return;
        await _tabManager.ToggleAudioOnlyAsync();
        TabStrip.Items.Refresh();
    }

    private async void DarkMode_Click(object sender, RoutedEventArgs e)
    {
        DarkModePopup.IsOpen = false;
        if (_tabManager is null) return;
        await _tabManager.ToggleDarkModeAsync();
        TabStrip.Items.Refresh();
        RefreshModeIndicators();
    }

    private void DarkMode_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DarkModePopup.IsOpen = true;
    }

    private async void ElementPicker_Click(object sender, RoutedEventArgs e)
    {
        BlockedElementsPopup.IsOpen = false;
        if (_tabManager is null) return;
        await _tabManager.StartElementPickerAsync();
        RefreshModeIndicators();
    }

    private void ElementPicker_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BlockedElementsPopup.IsOpen = true;
    }

    private void ManageBlockedElements_Click(object sender, RoutedEventArgs e)
    {
        BlockedElementsPopup.IsOpen = false;
        if (_tabManager is null) return;
        var window = new BlockedElementsWindow(_tabManager) { Owner = this };
        window.ShowDialog();
    }

    private async void ClearBlockedSite_Click(object sender, RoutedEventArgs e)
    {
        BlockedElementsPopup.IsOpen = false;
        if (_tabManager is not null) await _tabManager.ClearBlockedForCurrentHostAsync();
    }

    private async void ClearAllBlocked_Click(object sender, RoutedEventArgs e)
    {
        BlockedElementsPopup.IsOpen = false;
        if (_tabManager is not null) await _tabManager.ClearAllBlockedAsync();
    }

    private void DarkModeMenu_Opened(object sender, EventArgs e)
    {
        if (_tabManager?.ActiveTab is not BrowserTab tab) return;
        _updatingDarkModeMenu = true;
        DarkBrightnessSlider.Value = tab.DarkBrightness;
        DarkContrastSlider.Value = tab.DarkContrast;
        DarkSaturationSlider.Value = tab.DarkSaturation;
        _updatingDarkModeMenu = false;
    }

    private async void DarkModeParameter_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingDarkModeMenu || _tabManager is null) return;
        await _tabManager.SetDarkModeParametersAsync(
            DarkBrightnessSlider.Value,
            DarkContrastSlider.Value,
            DarkSaturationSlider.Value);
    }

    private async void ResetDarkMode_Click(object sender, RoutedEventArgs e)
    {
        _updatingDarkModeMenu = true;
        DarkBrightnessSlider.Value = 0.72;
        DarkContrastSlider.Value = 1.08;
        DarkSaturationSlider.Value = 0.88;
        _updatingDarkModeMenu = false;
        if (_tabManager is not null)
            await _tabManager.SetDarkModeParametersAsync(0.72, 1.08, 0.88);
    }

    private async Task NavigateFromAddressBarAsync()
    {
        if (_tabManager?.ActiveTab is null) return;
        await _tabManager.NavigateAsync(_tabManager.ActiveTab, AddressBox.Text);
        AddressBox.Text = _tabManager.ActiveTab.Address;
        AnimateWebViewTransition();
    }

    private void Back_Click(object sender, RoutedEventArgs e) { AnimateWebViewTransition(); _tabManager?.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { AnimateWebViewTransition(); _tabManager?.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) { AnimateWebViewTransition(); _tabManager?.Reload(); }
    private void Extensions_Click(object sender, RoutedEventArgs e)
    {
        if (_tabManager?.ExtensionManager is null) return;
        var window = new ExtensionsSettingsWindow(
            _tabManager.ExtensionManager,
            _tabManager.Environment,
            address => _tabManager!.OpenUrlAsync(address),
            _tabManager.ExtensionsAvailable)
        { Owner = this };
        window.ShowDialog();
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        var window = new ShortcutSettingsWindow { Owner = this };
        window.ShowDialog();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_appSettings, ApplyAppIdentity, ApplyAddressBarSettings, () => _topPanelVisible, ApplyWindowSettings) { Owner = this };
        window.ShowDialog();
    }

    private void ApplyWindowSettings(AppSettings settings) => _appSettings = settings;

    private void Downloads_Click(object sender, RoutedEventArgs e)
    {
        ToggleDownloadsDialog();
    }

    private void ToggleDownloadsDialog()
    {
        var core = _tabManager?.View.CoreWebView2;
        if (core is null) return;
        if (core.IsDefaultDownloadDialogOpen) core.CloseDefaultDownloadDialog();
        else core.OpenDefaultDownloadDialog();
    }

    private void ShowAddressBarFromMenu()
    {
        SetTopPanelVisible(true);
        SetAddressBarVisibility(true);
    }

    private void SetTopPanelVisible(bool visible)
    {
        _topPanelVisible = visible;
        TopPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AddressHotStrip.Visibility = visible && !_addressBarVisible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) ResetAddressBarTimer(); else _addressBarTimer.Stop();
        if (_appSettings.RememberTopPanelState && _appSettings.TopPanelHidden == visible)
            _appSettings = AppSettingsStore.Update(settings => settings.TopPanelHidden = !visible);
    }

    private void RestoreTopPanelState()
    {
        if (!_appSettings.RememberTopPanelState) return;

        var visible = !_appSettings.TopPanelHidden;
        _topPanelVisible = visible;
        TopPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AddressHotStrip.Visibility = visible && !_addressBarVisible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) ResetAddressBarTimer();
        else _addressBarTimer.Stop();
    }

    private void ApplyAddressBarSettings(AppSettings settings)
    {
        _appSettings = settings;
        _appSettings.AddressBarHideMode = string.Equals(settings.AddressBarHideMode, "Timer", StringComparison.OrdinalIgnoreCase) ? "Timer" : "Manual";
        _appSettings.AddressBarHideSeconds = Math.Clamp(settings.AddressBarHideSeconds, 2, 60);
        _addressBarTimer.Stop();
        _addressBarTimer.Interval = TimeSpan.FromSeconds(_appSettings.AddressBarHideSeconds);
        RestoreAddressBarState();
        if (_appSettings.AddressBarHideMode == "Timer") ResetAddressBarTimer();
    }

    private void SetAddressBarVisibility(bool visible, bool persist = true)
    {
        _addressBarVisible = visible;
        AddressRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        AddressHotStrip.Visibility = !visible && _topPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) ResetAddressBarTimer();
        else _addressBarTimer.Stop();
        if (persist && _appSettings.AddressBarHidden != !visible)
            _appSettings = AppSettingsStore.Update(settings => settings.AddressBarHidden = !visible);
    }

    private void RestoreAddressBarState()
    {
        SetAddressBarVisibility(!_appSettings.AddressBarHidden, persist: false);
    }

    private void ResetAddressBarTimer()
    {
        _addressBarTimer.Stop();
        if (_appSettings.AddressBarHideMode == "Timer" && _addressBarVisible && _topPanelVisible)
            _addressBarTimer.Start();
    }

    private void HideAddressBarIfAllowed()
    {
        if (!_topPanelVisible || !_addressBarVisible || _appSettings.AddressBarHideMode != "Timer") return;
        if (AddressRow.IsMouseOver || AddressRow.IsKeyboardFocusWithin || DarkModePopup.IsOpen || BlockedElementsPopup.IsOpen || _extensionPopupCount > 0 || _tabDragItem is not null || _draggedTabContainer is not null)
        {
            ResetAddressBarTimer();
            return;
        }
        SetAddressBarVisibility(false, persist: false);
    }

    private void AddressRow_MouseMove(object sender, WpfMouseEventArgs e) => ResetAddressBarTimer();
    private void AddressRow_KeyDown(object sender, WpfKeyEventArgs e) => ResetAddressBarTimer();
    private void AddressHotStrip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => SetAddressBarVisibility(true);

    private async void MainWindow_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var action = ShortcutManager.Instance.FindAction(key, Keyboard.Modifiers);
        if (action is null) return;
        if (action == ShortcutManager.ToggleAddressBar && !_topPanelVisible) return;
        e.Handled = true;
        switch (action)
        {
            case ShortcutManager.CloseTab when _tabManager?.ActiveTab is not null:
                await _tabManager.CloseTabAsync(_tabManager.ActiveTab);
                RefreshAddressBar();
                break;
            case ShortcutManager.NewTab when _tabManager is not null:
                await _tabManager.AddTabAsync();
                RefreshAddressBar();
                break;
            case ShortcutManager.ToggleTopPanel:
                SetTopPanelVisible(!_topPanelVisible);
                break;
            case ShortcutManager.ToggleAddressBar when _topPanelVisible:
                SetAddressBarVisibility(!_addressBarVisible);
                break;
            case ShortcutManager.OpenDownloads:
                ToggleDownloadsDialog();
                break;
        }
    }

    private void TabStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStartPoint = e.GetPosition(TabStrip);
        _tabDragItem = FindTabItem(e.OriginalSource as DependencyObject)?.DataContext as BrowserTab;
    }

    private void TabStrip_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var tab = FindTabItem(e.OriginalSource as DependencyObject)?.DataContext as BrowserTab;
        if (tab is null) return;
        e.Handled = true;
        OpenTabContextMenu(tab);
    }

    private void TabStrip_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ScrollTabs(e.Delta);
        e.Handled = true;
    }

    private void TabRow_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ScrollTabs(e.Delta);
        e.Handled = true;
    }

    private void ScrollTabs(int delta)
    {
        var panel = TabStrip.Template?.FindName("TabLayoutPanel", TabStrip) as TabShrinkPanel;
        panel?.ScrollBy(delta);
    }

    private void OpenTabContextMenu(BrowserTab tab)
    {
        _tabContextPopup?.SetCurrentValue(Popup.IsOpenProperty, false);
        var menu = new StackPanel();
        void AddItem(string text, Action action)
        {
            var button = new System.Windows.Controls.Button { Content = text, Style = (Style)FindResource("FoxMenuButton") };
            button.Click += (_, _) => { _tabContextPopup!.IsOpen = false; action(); };
            menu.Children.Add(button);
        }
        AddItem("Дублювати", () => _ = DuplicateTabFromMenuAsync(tab));
        AddItem("Копіювати посилання", () => _ = CopyTabAddressAsync(tab.Address));
        AddItem("Перейменувати…", () => RenameTabFromMenu(tab));
        var colorRow = new WrapPanel { Margin = new Thickness(8, 4, 8, 2) };
        foreach (var color in new[] { "#FF6B6B", "#FFA94D", "#FFD43B", "#69DB7C", "#3BC9DB", "#78AFFF", "#B197FC", "#F783AC" })
        {
            var swatch = new System.Windows.Controls.Button
            {
                Width = 22, Height = 22, Margin = new Thickness(2), Padding = new Thickness(0),
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White),
                BorderThickness = new Thickness(1), ToolTip = color
            };
            swatch.Click += (_, _) => { tab.ColorHex = color; _tabContextPopup!.IsOpen = false; _ = _tabManager?.SaveSessionAsync(); };
            colorRow.Children.Add(swatch);
        }
        var noColor = new System.Windows.Controls.Button { Content = "Без кольору", Style = (Style)FindResource("FoxMenuButton") };
        noColor.Click += (_, _) => { tab.ColorHex = null; _tabContextPopup!.IsOpen = false; _ = _tabManager?.SaveSessionAsync(); };
        menu.Children.Add(colorRow);
        menu.Children.Add(noColor);
        _tabContextPopup = new Popup
        {
            Placement = PlacementMode.MousePoint, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 27, 34)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 64, 78)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Child = menu
            }
        };
        _tabContextPopup.IsOpen = true;
    }

    private async Task DuplicateTabFromMenuAsync(BrowserTab source)
    {
        if (_tabManager is null) return;
        var selected = TabStrip.SelectedItem;
        await _tabManager.DuplicateTab(source);
        if (selected is not null) TabStrip.SelectedItem = selected;
    }

    private async Task CopyTabAddressAsync(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { System.Windows.Clipboard.SetDataObject(text, true); return; }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 2) { await Task.Delay(50); }
        }
    }

    private void RenameTabFromMenu(BrowserTab tab)
    {
        var window = new TabRenameWindow(tab.CustomTitle ?? tab.Title) { Owner = this };
        if (window.ShowDialog() != true) return;
        tab.CustomTitle = window.CustomTitle;
        _ = _tabManager?.SaveSessionAsync();
    }

    private void TabStrip_PreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragItem is null) return;
        var position = e.GetPosition(TabStrip);
        if (Math.Abs(position.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _tabDragItem;
        _tabDragItem = null;
        _draggedTabContainer = FindTabItem(e.OriginalSource as DependencyObject);
        if (_draggedTabContainer is not null)
        {
            _draggedTabContainer.Opacity = 0.7;
            _draggedTabContainer.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            _draggedTabContainer.RenderTransform = new System.Windows.Media.ScaleTransform(0.97, 0.97);
        }
        try
        {
            System.Windows.DragDrop.DoDragDrop(TabStrip, item, WpfDragDropEffects.Move);
        }
        finally
        {
            RestoreDraggedTabVisual();
            ResetDragPreview();
            ResetAddressBarTimer();
        }
    }

    private void TabStrip_DragOver(object sender, WpfDragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(BrowserTab)) ? WpfDragDropEffects.Move : WpfDragDropEffects.None;
        if (e.Data.GetDataPresent(typeof(BrowserTab)))
        {
            var target = FindTabItem(e.OriginalSource as DependencyObject);
            if (target is not null && !ReferenceEquals(target, _draggedTabContainer) && !ReferenceEquals(target, _dragPreviewTarget))
            {
                ResetDragPreview();
                _dragPreviewTarget = target;
                var source = e.Data.GetData(typeof(BrowserTab)) as BrowserTab;
                var targetTab = target.DataContext as BrowserTab;
                var sourceIndex = source is null ? -1 : _tabManager?.Tabs.IndexOf(source) ?? -1;
                var targetIndex = targetTab is null ? -1 : _tabManager?.Tabs.IndexOf(targetTab) ?? -1;
                var offset = sourceIndex >= 0 && targetIndex >= 0 && sourceIndex < targetIndex ? -6 : 6;
                var translation = new System.Windows.Media.TranslateTransform();
                target.RenderTransform = translation;
                translation.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                    new DoubleAnimation(offset, new Duration(TimeSpan.FromMilliseconds(120))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        e.Handled = true;
    }

    private async void TabStrip_Drop(object sender, WpfDragEventArgs e)
    {
        if (_tabManager is null || !e.Data.GetDataPresent(typeof(BrowserTab))) return;
        var source = e.Data.GetData(typeof(BrowserTab)) as BrowserTab;
        var target = FindTabItem(e.OriginalSource as DependencyObject)?.DataContext as BrowserTab;
        if (source is null || target is null || ReferenceEquals(source, target)) return;
        var targetIndex = _tabManager.Tabs.IndexOf(target);
        if (targetIndex >= 0) await _tabManager.MoveTabAsync(source, targetIndex);
        e.Handled = true;
    }

    private void RestoreDraggedTabVisual()
    {
        if (_draggedTabContainer is null) return;
        _draggedTabContainer.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.7, 1, new Duration(TimeSpan.FromMilliseconds(140))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        if (_draggedTabContainer.RenderTransform is WpfScaleTransform scale)
        {
            scale.BeginAnimation(WpfScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.97, 1, new Duration(TimeSpan.FromMilliseconds(140))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            scale.BeginAnimation(WpfScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.97, 1, new Duration(TimeSpan.FromMilliseconds(140))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
        _draggedTabContainer = null;
    }

    private void ResetDragPreview()
    {
        if (_dragPreviewTarget?.RenderTransform is WpfTranslateTransform translation)
        {
            translation.BeginAnimation(WpfTranslateTransform.XProperty,
                new DoubleAnimation(translation.X, 0, new Duration(TimeSpan.FromMilliseconds(120))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
        _dragPreviewTarget = null;
    }

    private void AnimateWebViewTransition()
    {
        WebViewHost.BeginAnimation(UIElement.OpacityProperty, null);
        WebViewHost.Opacity = 0.85;
        WebViewHost.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.85, 1, new Duration(TimeSpan.FromMilliseconds(130))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private static TabItem? FindTabItem(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TabItem tabItem) return tabItem;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void PinnedExtension_Click(object sender, RoutedEventArgs e)
    {
        if (_tabManager?.ExtensionManager is null || sender is not System.Windows.Controls.Button button || button.DataContext is not PinnedExtensionItem item)
            return;

        if (string.IsNullOrWhiteSpace(item.PopupPage))
        {
            System.Windows.MessageBox.Show(
                this,
                $"Розширення «{item.Name}» не має popup-вікна у manifest.json. Це нормально для частини extension (наприклад, Shazam). Відкриваю список розширень, де можна перевірити його налаштування.",
                "Popup розширення",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Extensions_Click(sender, e);
            return;
        }

        var screenPositionDevice = button.PointToScreen(new System.Windows.Point(0, button.ActualHeight));
        var source = PresentationSource.FromVisual(button);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var screenPosition = fromDevice.Transform(screenPositionDevice);

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)screenPositionDevice.X, (int)screenPositionDevice.Y));
        var workAreaTopLeft = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var workAreaBottomRight = fromDevice.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        var workingArea = new System.Windows.Rect(workAreaTopLeft, workAreaBottomRight);

        var popup = new ExtensionPopupWindow(_tabManager.Environment, item.Extension.Id, item.PopupPage, screenPosition, workingArea)
        {
            Owner = this
        };
        _extensionPopupCount++;
        popup.Closed += (_, _) =>
        {
            _extensionPopupCount = Math.Max(0, _extensionPopupCount - 1);
            ResetAddressBarTimer();
        };
        popup.Show();
    }

    private async void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != TabStrip || TabStrip.SelectedItem is not BrowserTab tab || _tabManager is null || !_tabManager.SessionReady) return;
        AddressBox.Text = tab.Address;
        await _tabManager.SwitchToTabAsync(tab);
        AnimateWebViewTransition();
        RefreshAddressBar();
        RefreshModeIndicators();
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is BrowserTab tab && _tabManager is not null)
            await _tabManager.CloseTabAsync(tab);
        RefreshAddressBar();
    }

    private async void Window_StateChanged(object? sender, EventArgs e)
    {
        var minimized = WindowState == WindowState.Minimized;
        if (minimized) _tray?.MinimizeToTray();
        else ScheduleGeometrySave();
        if (_tabManager is not null) await _tabManager.SetMinimizedModeAsync(minimized);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleGeometrySave();

    private void Window_LocationChanged(object? sender, EventArgs e) => ScheduleGeometrySave();

    private void ScheduleGeometrySave()
    {
        if (!_geometryReady || WindowState == WindowState.Minimized || !IsVisible) return;
        _geometrySaveTimer.Stop();
        _geometrySaveTimer.Start();
    }

    private void SaveWindowGeometry()
    {
        if (WindowState == WindowState.Minimized || !IsVisible) return;
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (!IsValidGeometry(bounds.Width, bounds.Height)) return;

        var maximized = WindowState == WindowState.Maximized;
        var changed = _appSettings.WindowLeft != bounds.Left ||
                      _appSettings.WindowTop != bounds.Top ||
                      _appSettings.WindowWidth != bounds.Width ||
                      _appSettings.WindowHeight != bounds.Height ||
                      _appSettings.WindowMaximized != maximized;
        if (!changed) return;
        _appSettings = AppSettingsStore.Update(settings =>
        {
            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
            settings.WindowMaximized = maximized;
        });
    }

    private void RestoreWindowGeometry(AppSettings settings)
    {
        if (!settings.WindowLeft.HasValue || !settings.WindowTop.HasValue ||
            !settings.WindowWidth.HasValue || !settings.WindowHeight.HasValue)
            return;

        var workArea = SystemParameters.WorkArea;
        if (!IsValidGeometry(settings.WindowWidth.Value, settings.WindowHeight.Value) ||
            double.IsNaN(settings.WindowLeft.Value) || double.IsInfinity(settings.WindowLeft.Value) ||
            double.IsNaN(settings.WindowTop.Value) || double.IsInfinity(settings.WindowTop.Value))
            return;
        var width = Math.Clamp(settings.WindowWidth.Value, MinWidth, workArea.Width);
        var height = Math.Clamp(settings.WindowHeight.Value, MinHeight, workArea.Height);
        if (!IsValidGeometry(width, height)) return;

        var left = settings.WindowLeft.Value;
        var top = settings.WindowTop.Value;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        var intersectionWidth = Math.Max(0, Math.Min(left + width, virtualRight) - Math.Max(left, virtualLeft));
        var intersectionHeight = Math.Max(0, Math.Min(top + height, virtualBottom) - Math.Max(top, virtualTop));
        var visible = intersectionWidth >= 100 && intersectionHeight >= 50;

        Width = width;
        Height = height;
        if (visible)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (settings.WindowMaximized && visible) WindowState = WindowState.Maximized;
    }

    private static bool IsValidGeometry(double width, double height) =>
        !double.IsNaN(width) && !double.IsInfinity(width) && width > 0 &&
        !double.IsNaN(height) && !double.IsInfinity(height) && height > 0;

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _geometrySaveTimer.Stop();
            SaveWindowGeometry();
            _tray?.MinimizeToTray();
            if (_tabManager is not null) await _tabManager.SetMinimizedModeAsync(true);
        }
        else
        {
            if (!_closingAfterSessionSave)
            {
                e.Cancel = true;
                _closingAfterSessionSave = true;
                if (_tabManager is not null) await _tabManager.SaveSessionAsync();
                Close();
                return;
            }
            _geometrySaveTimer.Stop();
            SaveWindowGeometry();
            _watchdogTimer.Stop();
            _addressBarTimer.Stop();
            _tray?.Dispose();
            _audioLabeler?.Dispose();
            if (_tabManager is not null) await _tabManager.DisposeAsync();
        }
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    private void ApplyDarkTitleBar(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.ApplyDarkTitleBar(hwnd, enabled);
    }

    private void RefreshAddressBar()
    {
        if (_tabManager?.ActiveTab is not null)
        {
            if (!ReferenceEquals(TabStrip.SelectedItem, _tabManager.ActiveTab)) TabStrip.SelectedItem = _tabManager.ActiveTab;
            AddressBox.Text = _tabManager.ActiveTab.Address;
        }
    }

    private void RefreshModeIndicators()
    {
        if (!IsInitialized) return;
        var darkActive = _tabManager?.ActiveTab?.IsDarkMode == true;
        var pickerActive = _tabManager?.IsElementPickerActive == true;
        var activeBackground = (System.Windows.Media.Brush)FindResource("AurBlueSoft");
        var activeBorder = (System.Windows.Media.Brush)FindResource("AurBlue");
        var inactiveBackground = (System.Windows.Media.Brush)FindResource("CardBg");
        var inactiveBorder = (System.Windows.Media.Brush)FindResource("BorderMedium");

        DarkModeButton.Background = darkActive ? activeBackground : inactiveBackground;
        DarkModeButton.BorderBrush = darkActive ? activeBorder : inactiveBorder;
        DarkModeButton.Foreground = darkActive
            ? (System.Windows.Media.Brush)FindResource("TextPrimary")
            : (System.Windows.Media.Brush)FindResource("TextSecondary");

        ElementPickerButton.Background = pickerActive ? activeBackground : inactiveBackground;
        ElementPickerButton.BorderBrush = pickerActive ? activeBorder : inactiveBorder;
        ElementPickerButton.Foreground = pickerActive
            ? (System.Windows.Media.Brush)FindResource("TextPrimary")
            : (System.Windows.Media.Brush)FindResource("TextSecondary");
    }
}
