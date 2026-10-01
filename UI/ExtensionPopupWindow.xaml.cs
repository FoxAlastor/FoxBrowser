using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using FoxBrowser.Native;

namespace FoxBrowser.UI;

public partial class ExtensionPopupWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private readonly string _extensionId;
    private readonly string _popupPage;

    public ExtensionPopupWindow(
        CoreWebView2Environment environment,
        string extensionId,
        string popupPage,
        System.Windows.Point screenPosition,
        System.Windows.Rect workingArea)
    {
        InitializeComponent();
        _environment = environment;
        _extensionId = extensionId;
        _popupPage = popupPage;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => Win32Interop.RemovePopupFrame(new WindowInteropHelper(this).Handle);

        Width = Math.Min(Width, Math.Max(220, workingArea.Width - 8));
        Height = Math.Min(Height, Math.Max(160, workingArea.Height - 8));

        // Keep the popup on the monitor where the extension button lives.
        // If there is no room below the toolbar, open it above the button.
        var left = Math.Min(screenPosition.X, workingArea.Right - Width - 4);
        left = Math.Max(left, workingArea.Left + 4);
        var top = screenPosition.Y + 4;
        if (top + Height > workingArea.Bottom - 4)
            top = screenPosition.Y - Height - 4;
        top = Math.Max(top, workingArea.Top + 4);
        Left = left;
        Top = top;
        Deactivated += (_, _) => Close();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var view = new WebView2();
            PopupHost.Children.Add(view);
            await view.EnsureCoreWebView2Async(_environment);
            view.CoreWebView2.Navigate($"chrome-extension://{_extensionId}/{_popupPage}");
        }
        catch
        {
            Close();
        }
    }
}
