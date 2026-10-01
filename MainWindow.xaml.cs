using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using FoxBrowser.Core;
using FoxBrowser.Native;
using FoxBrowser.UI;

namespace FoxBrowser;

public partial class MainWindow : Window
{
    private TabManager? _tabManager;
    private TrayIconManager? _tray;
    private bool _allowClose;
    private readonly DispatcherTimer _watchdogTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private AudioSessionLabeler? _audioLabeler;
    private bool _updatingDarkModeMenu;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "FoxBrowser.ico");
            if (File.Exists(icoPath))
                Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(icoPath, UriKind.Absolute));
        }
        catch { }
        SourceInitialized += (_, _) => ApplyDarkTitleBar(true);
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _tray = new TrayIconManager(this, ExitApplication);
            var environmentFactory = new WebView2EnvironmentFactory();
            var recoveryMode = false;
            CoreWebView2Environment environment;
            try
            {
                environment = await environmentFactory.CreateAsync();
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
            var startupNotice = environmentFactory.TakeStartupNotice();
            if (startupNotice is not null)
                System.Windows.MessageBox.Show(this, startupNotice, "FoxBrowser", MessageBoxButton.OK, MessageBoxImage.Information);
            _tabManager = new TabManager(WebViewHost, environment, !recoveryMode);
            _tabManager.ActiveTabStateChanged += (_, _) => Dispatcher.Invoke(RefreshAddressBar);
            DataContext = _tabManager;
            await _tabManager.InitializeAsync();
            var iconPath = Path.Combine(AppContext.BaseDirectory, "FoxBrowser.ico");
            try
            {
                _audioLabeler = new AudioSessionLabeler((uint)Process.GetCurrentProcess().Id, iconPath);
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
        }
        catch (Exception ex)
        {
            BrowserStatus.Text = $"WebView2 не запустився: {ex.Message}";
            BrowserStatus.Visibility = Visibility.Visible;
        }
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
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _tabManager?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _tabManager?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => _tabManager?.Reload();
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
        popup.Show();
    }

    private async void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != TabStrip || TabStrip.SelectedItem is not BrowserTab tab || _tabManager is null) return;
        AddressBox.Text = tab.Address;
        await _tabManager.SwitchToTabAsync(tab);
        RefreshAddressBar();
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is BrowserTab tab) _tabManager?.CloseTab(tab);
        RefreshAddressBar();
    }

    private async void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_tabManager is null) return;
        var minimized = WindowState == WindowState.Minimized;
        if (minimized) _tray?.MinimizeToTray();
        await _tabManager.SetMinimizedModeAsync(minimized);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            _tray?.MinimizeToTray();
            if (_tabManager is not null) await _tabManager.SetMinimizedModeAsync(true);
        }
        else
        {
            _watchdogTimer.Stop();
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
}
