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

    private const int WmNcRButtonDown = 0x00A4;
    private const int WmNcRButtonUp = 0x00A5;
    private const int HtSysMenu = 3;

    public MainWindow()
    {
        InitializeComponent();
        _appSettings = AppSettingsStore.Load();
        ApplyAppIdentity(_appSettings);
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyDarkTitleBar(true);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(MainWindowWndProc);
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
                Children = { item, shortcutsItem }
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
        try
        {
            _tray = new TrayIconManager(this, ExitApplication);
            _tray.UpdateIdentity(Title, ResolveAppIconPath(_appSettings));
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
            _tabManager.ActiveTabStateChanged += (_, _) => Dispatcher.Invoke(() =>
            {
                RefreshAddressBar();
                RefreshModeIndicators();
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

    private async void MainWindow_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var action = ShortcutManager.Instance.FindAction(key, Keyboard.Modifiers);
        if (action is null) return;
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
                _topPanelVisible = !_topPanelVisible;
                TopPanel.Visibility = _topPanelVisible ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }

    private void TabStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStartPoint = e.GetPosition(TabStrip);
        _tabDragItem = FindTabItem(e.OriginalSource as DependencyObject)?.DataContext as BrowserTab;
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
            if (!_closingAfterSessionSave)
            {
                e.Cancel = true;
                _closingAfterSessionSave = true;
                if (_tabManager is not null) await _tabManager.SaveSessionAsync();
                Close();
                return;
            }
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
