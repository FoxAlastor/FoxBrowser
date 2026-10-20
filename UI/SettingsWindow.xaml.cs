using System.Windows;
using FoxBrowser.Core;

namespace FoxBrowser.UI;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings, Action<AppSettings> applyIdentity, Action<AppSettings> applyAddressBar)
    {
        InitializeComponent();
        IdentityPanel.Initialize(settings, applyIdentity);
        AddressPanel.Initialize(settings);
        AddressPanel.SettingsChanged += updated =>
        {
            applyAddressBar(updated);
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
