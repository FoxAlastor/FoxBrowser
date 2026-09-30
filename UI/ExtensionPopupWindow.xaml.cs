using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FoxBrowser.UI;

public partial class ExtensionPopupWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private readonly string _extensionId;
    private readonly string _popupPage;

    public ExtensionPopupWindow(CoreWebView2Environment environment, string extensionId, string popupPage, System.Drawing.Point screenPosition)
    {
        InitializeComponent();
        _environment = environment;
        _extensionId = extensionId;
        _popupPage = popupPage;
        Left = screenPosition.X;
        Top = screenPosition.Y;
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
