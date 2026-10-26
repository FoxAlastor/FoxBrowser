using System.Windows;
using System.Windows.Interop;
using FoxBrowser.Core;
using FoxBrowser.Native;

namespace FoxBrowser.UI;

public partial class AppSettingsWindow : Window
{
    public AppSettingsWindow(AppSettings current, Action<AppSettings> apply)
    {
        InitializeComponent();
        SourceInitialized += AppSettingsWindow_SourceInitialized;
        ContentRendered += AppSettingsWindow_ContentRendered;
        var panel = new AppIdentityPanel(current, apply);
        panel.Applied += (_, _) => DialogResult = true;
        IdentityHost.Children.Add(panel);
    }

    private void AppSettingsWindow_SourceInitialized(object? sender, EventArgs e)
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

    private void AppSettingsWindow_ContentRendered(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            Win32Interop.SetWindowCloaked(hwnd, false);
        }));
    }
}
