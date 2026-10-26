using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FoxBrowser.Core;
using FoxBrowser.Native;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;

namespace FoxBrowser.UI;

public partial class ExtensionsSettingsWindow : Window
{
    private readonly ExtensionManager _manager;
    private readonly CoreWebView2Environment _environment;
    private readonly Func<string, Task> _openUrl;
    private WebView2? _optionsView;

    public ExtensionsSettingsWindow(ExtensionManager manager, CoreWebView2Environment environment, Func<string, Task> openUrl, bool extensionsAvailable)
    {
        InitializeComponent();
        SourceInitialized += ExtensionsSettingsWindow_SourceInitialized;
        _manager = manager;
        _environment = environment;
        _openUrl = openUrl;
        ExtensionList.ItemsSource = _manager.Extensions;
        if (!extensionsAvailable)
        {
            ExtensionsStatus.Text = "Розширення недоступні в цьому сеансі: FoxBrowser запустився у режимі відновлення профілю. Перезапусти FoxBrowser для повернення до звичайного профілю.";
            ExtensionList.IsEnabled = false;
        }
        Loaded += async (_, _) =>
        {
            try
            {
                await _manager.RefreshAsync();
                Dispatcher.Invoke(RefreshPinCheckboxes);
            }
            finally
            {
                RevealWindowAfterStartup();
            }
        };
    }

    private void ExtensionsSettingsWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.ApplyDarkTitleBar(hwnd, true);
        Win32Interop.SetWindowCaptionColor(hwnd, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF);
        Win32Interop.SetWindowClassBackground(hwnd, 0x15, 0x17, 0x1A);
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is not null)
            source.CompositionTarget.BackgroundColor = System.Windows.Media.Color.FromRgb(0x15, 0x17, 0x1A);
        Win32Interop.SetWindowCloaked(hwnd, true);
    }

    private void RevealWindowAfterStartup()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            Win32Interop.SetWindowCloaked(hwnd, false);
        }));
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Виберіть розпаковану папку extension з manifest.json" };
        var owner = new WindowInteropHelper(this).Handle;
        if (dialog.ShowDialog(new NativeWindowWrapper(owner)) != Forms.DialogResult.OK) return;
        try
        {
            await _manager.AddExtensionAsync(dialog.SelectedPath);
            Dispatcher.Invoke(RefreshPinCheckboxes);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void AddArchive_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Виберіть архів extension",
            Filter = "Extension archive (*.zip;*.crx)|*.zip;*.crx|Усі файли (*.*)|*.*",
            CheckFileExists = true
        };
        var owner = new WindowInteropHelper(this).Handle;
        if (dialog.ShowDialog(new NativeWindowWrapper(owner)) != Forms.DialogResult.OK) return;
        try
        {
            await _manager.AddArchiveAsync(dialog.FileName);
            ExtensionsStatus.Text = "Розширення розпаковано у профіль FoxBrowser та підключено.";
            Dispatcher.Invoke(RefreshPinCheckboxes);
        }
        catch (Exception ex) { ShowError($"Не вдалося імпортувати архів: {ex.Message}"); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ExtensionManager.OpenExtensionsFolder();
            ExtensionsStatus.Text = $"Папка розширень: {ExtensionManager.ExtensionsRootPath}";
        }
        catch (Exception ex) { ShowError($"Не вдалося відкрити папку розширень: {ex.Message}"); }
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfCheckBox)?.Tag is not CoreWebView2BrowserExtension extension) return;
        var checkBox = (WpfCheckBox)sender;
        try { await _manager.SetEnabledAsync(extension, checkBox.IsChecked == true); }
        catch (Exception ex)
        {
            checkBox.IsChecked = extension.IsEnabled;
            ShowError(ex.Message);
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfCheckBox)?.Tag is CoreWebView2BrowserExtension extension && sender is WpfCheckBox checkBox)
            _manager.SetPinned(extension, checkBox.IsChecked == true);
    }

    private async void Options_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfButton)?.Tag is not CoreWebView2BrowserExtension extension) return;
        var optionsPage = _manager.GetOptionsPage(extension.Id);
        if (string.IsNullOrWhiteSpace(optionsPage))
        {
            ShowError("Для цього extension не знайдено options_ui.page або options_page. Видали його та імпортуй папку ще раз, щоб FoxBrowser прочитав manifest.json.");
            return;
        }
        try
        {
            _optionsView ??= await CreateOptionsViewAsync();
            ExtensionList.Visibility = Visibility.Collapsed;
            OptionsHost.Visibility = Visibility.Visible;
            _optionsView.CoreWebView2.Navigate($"chrome-extension://{extension.Id}/{optionsPage}");
        }
        catch (Exception ex) { ShowError($"Не вдалося відкрити налаштування extension: {ex.Message}"); }
    }

    private void BackToList_Click(object sender, RoutedEventArgs e)
    {
        OptionsHost.Visibility = Visibility.Collapsed;
        ExtensionList.Visibility = Visibility.Visible;
    }

    private async Task<WebView2> CreateOptionsViewAsync()
    {
        var view = new WebView2 { Visibility = Visibility.Visible };
        OptionsWebViewHost.Children.Add(view);
        await view.EnsureCoreWebView2Async(_environment);
        return view;
    }

    private async void Store_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Close();
            await _openUrl("https://chromewebstore.google.com/category/extensions");
        }
        catch (Exception ex)
        {
            ShowError($"Не вдалося відкрити Chrome Web Store: {ex.Message}");
        }
    }

    private async void Downloader_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Close();
            await _openUrl("https://www.crx4chrome.com/crx-downloader/");
        }
        catch (Exception ex)
        {
            ShowError($"Не вдалося відкрити CRX Downloader: {ex.Message}");
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as WpfButton)?.Tag is CoreWebView2BrowserExtension extension)
        {
            try { await _manager.RemoveAsync(extension); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
    }

    private void RefreshPinCheckboxes()
    {
        foreach (var extension in _manager.Extensions)
        {
            if (ExtensionList.ItemContainerGenerator.ContainerFromItem(extension) is not ListBoxItem item) continue;
            var pin = FindVisualChild<WpfCheckBox>(item, "PinCheckBox");
            if (pin is not null) pin.IsChecked = _manager.IsPinned(extension);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && match.Name == name) return match;
            var nested = FindVisualChild<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void ShowError(string message) => System.Windows.MessageBox.Show(this, message, "Розширення", MessageBoxButton.OK, MessageBoxImage.Error);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _optionsView?.Dispose();
        base.OnClosed(e);
    }

    private sealed class NativeWindowWrapper : System.Windows.Forms.IWin32Window
    {
        public NativeWindowWrapper(nint handle) => Handle = handle;
        public nint Handle { get; }
    }
}
