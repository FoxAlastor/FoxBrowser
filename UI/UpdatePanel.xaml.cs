using System.Diagnostics;
using System.Windows;
using FoxBrowser.Core;

namespace FoxBrowser.UI;

public partial class UpdatePanel : System.Windows.Controls.UserControl
{
    private readonly UpdateService _service = new();
    private CancellationTokenSource? _cancellation;
    private UpdateCheckResult? _latest;

    public UpdatePanel()
    {
        InitializeComponent();
        CurrentVersionText.Text = $"Поточна версія: {UpdateService.CurrentVersion}";
        Unloaded += (_, _) => _cancellation?.Cancel();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not null) return;
        _cancellation = new CancellationTokenSource();
        SetChecking(true);
        try
        {
            _latest = await _service.CheckLatestAsync(_cancellation.Token);
            ReleaseButton.IsEnabled = IsTrustedReleaseUrl(_latest.Info.PageUrl);
            NotesText.Text = Shorten(_latest.Info.Notes);
            InstallButton.IsEnabled = _latest.IsNewer && !string.IsNullOrWhiteSpace(_latest.Info.AssetUrl);
            StatusText.Text = _latest.Notice ?? (_latest.IsNewer ? $"Доступна версія {_latest.Info.Latest}." : "Встановлено останню версію.");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Перевірку скасовано.";
        }
        catch (UpdateServiceException ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            SetChecking(false);
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_latest?.Info.AssetUrl is null || !_latest.IsNewer || _cancellation is not null) return;
        var answer = System.Windows.MessageBox.Show(
            System.Windows.Window.GetWindow(this),
            "Застосунок буде закрито й перезапущено. Сесію буде збережено. Продовжити?",
            "Оновлення FoxBrowser",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _cancellation = new CancellationTokenSource();
        SetDownloading(true);
        try
        {
            var progress = new Progress<double>(value => DownloadProgress.Value = value);
            var script = await _service.DownloadAsync(_latest.Info, progress, _cancellation.Token);
            var updateDirectory = System.IO.Path.GetDirectoryName(script)!;
            var started = UpdateService.StartInstaller(script, updateDirectory);
            if (!started)
            {
                StatusText.Text = "Не вдалося запустити оновлювач. Застосунок не закрито.";
                return;
            }
            System.Windows.Window.GetWindow(this)?.Close();
            Dispatcher.BeginInvoke(new Action(() =>
                (System.Windows.Application.Current.MainWindow as FoxBrowser.MainWindow)?.ExitApplication()));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Завантаження скасовано.";
        }
        catch (UpdateServiceException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Не вдалося підготувати оновлення: {ex.Message}";
        }
        finally
        {
            SetDownloading(false);
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private void Release_Click(object sender, RoutedEventArgs e)
    {
        if (_latest is null || !IsTrustedReleaseUrl(_latest.Info.PageUrl)) return;
        try { Process.Start(new ProcessStartInfo(_latest.Info.PageUrl) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Не вдалося відкрити сторінку релізу: {ex.Message}"; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void SetChecking(bool active)
    {
        CheckButton.IsEnabled = !active;
        InstallButton.IsEnabled = !active && _latest?.IsNewer == true && !string.IsNullOrWhiteSpace(_latest.Info.AssetUrl);
        CancelButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = active ? "Перевіряю GitHub…" : StatusText.Text;
    }

    private void SetDownloading(bool active)
    {
        CheckButton.IsEnabled = !active;
        InstallButton.IsEnabled = !active && _latest?.IsNewer == true && !string.IsNullOrWhiteSpace(_latest.Info.AssetUrl);
        ReleaseButton.IsEnabled = !active && _latest is not null && IsTrustedReleaseUrl(_latest.Info.PageUrl);
        CancelButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active) DownloadProgress.Value = 0;
    }

    private static string Shorten(string text) => text.Length <= 600 ? text : text[..600] + "…";
    private static bool IsTrustedReleaseUrl(string url) => url.StartsWith("https://github.com/FoxAlastor/FoxBrowser/", StringComparison.Ordinal);
}
