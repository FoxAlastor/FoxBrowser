using System.Windows;
using System.Windows.Interop;
using FoxBrowser.Core;
using FoxBrowser.Native;

namespace FoxBrowser.UI;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings, Action<AppSettings> applyIdentity, Action<AppSettings> applyAddressBar, Func<bool> isTopPanelVisible, Action<AppSettings> applyWindowSettings)
    {
        InitializeComponent();
        SourceInitialized += SettingsWindow_SourceInitialized;
        ContentRendered += SettingsWindow_ContentRendered;
        IdentityPanel.Initialize(settings, applyIdentity);
        AddressPanel.Initialize(settings);
        AddressPanel.SettingsChanged += updated =>
        {
            applyAddressBar(updated);
        };
        WindowPanel.Initialize(settings, isTopPanelVisible, applyWindowSettings);
    }

    private void SettingsWindow_SourceInitialized(object? sender, EventArgs e)
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

    private void SettingsWindow_ContentRendered(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            Win32Interop.SetWindowCloaked(hwnd, false);
        }));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
