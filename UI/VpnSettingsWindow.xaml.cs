using System.Windows;
using FoxBrowser.Core;
using Forms = System.Windows.Forms;

namespace FoxBrowser.UI;

public partial class VpnSettingsWindow : Window
{
    private readonly VpnManager _vpn;

    public VpnSettingsWindow(VpnManager vpn)
    {
        InitializeComponent();
        _vpn = vpn;
        ProfileList.ItemsSource = _vpn.Profiles;
        _vpn.StateChanged += OnStateChanged;
        RefreshState();
        Closed += (_, _) => _vpn.StateChanged -= OnStateChanged;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Filter = "WireGuard config (*.conf)|*.conf|All files (*.*)|*.*",
            Title = "Імпортувати WireGuard конфігурацію"
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            var profile = _vpn.ImportTunnelConfig(dialog.FileName);
            ProfileList.SelectedItem = profile;
            RefreshState();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not VpnProfile profile)
        {
            ShowError("Спочатку імпортуйте або виберіть профіль .conf.");
            return;
        }
        try { await _vpn.ConnectAsync(profile); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { RefreshState(); }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        try { await _vpn.DisconnectAsync(); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { RefreshState(); }
    }

    private void OnStateChanged(object? sender, EventArgs e) => Dispatcher.Invoke(RefreshState);

    private void RefreshState()
    {
        StatusText.Text = _vpn.State.ToString();
        ErrorText.Text = _vpn.LastError ?? string.Empty;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        System.Windows.MessageBox.Show(this, message, "VPN", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
