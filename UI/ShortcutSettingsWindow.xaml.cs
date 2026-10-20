using System.Windows;

namespace FoxBrowser.UI;

public partial class ShortcutSettingsWindow : Window
{
    public ShortcutSettingsWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
