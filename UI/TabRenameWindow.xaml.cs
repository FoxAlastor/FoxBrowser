using System.Windows;
using System.Windows.Input;

namespace FoxBrowser.UI;

public partial class TabRenameWindow : Window
{
    public TabRenameWindow(string title)
    {
        InitializeComponent();
        TitleBox.Text = title;
        TitleBox.SelectAll();
        Loaded += (_, _) => TitleBox.Focus();
    }

    public string? CustomTitle => string.IsNullOrWhiteSpace(TitleBox.Text) ? null : TitleBox.Text.Trim();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }
}
