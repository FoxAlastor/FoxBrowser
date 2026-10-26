using System.Windows;

namespace FoxBrowser;

public partial class App : System.Windows.Application
{
    public static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> EnvironmentTask { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        FoxBrowser.Core.StartupTrace.Mark("App.OnStartup");
        System.Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "FF0B0E12");
        EnvironmentTask = new FoxBrowser.Core.WebView2EnvironmentFactory().CreateAsync();
        FoxBrowser.Core.StartupTrace.Mark("WebView2 environment creation started");
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }
}
