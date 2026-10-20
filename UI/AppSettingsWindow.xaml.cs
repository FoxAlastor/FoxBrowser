using System.Windows;
using FoxBrowser.Core;

namespace FoxBrowser.UI;

public partial class AppSettingsWindow : Window
{
    public AppSettingsWindow(AppSettings current, Action<AppSettings> apply)
    {
        InitializeComponent();
        var panel = new AppIdentityPanel(current, apply);
        panel.Applied += (_, _) => DialogResult = true;
        IdentityHost.Children.Add(panel);
    }
}
