using System.Windows;
using FoxBrowser.Core;

namespace FoxBrowser.UI;

public partial class WindowSettingsPanel : System.Windows.Controls.UserControl
{
    private readonly ShortcutManager _shortcuts = ShortcutManager.Instance;
    private bool _loading;
    private AppSettings _settings = new();
    private Func<bool>? _isTopPanelVisible;
    private Action<AppSettings>? _apply;

    public WindowSettingsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _shortcuts.Changed += Shortcuts_Changed;
            Refresh();
        };
        Unloaded += (_, _) => _shortcuts.Changed -= Shortcuts_Changed;
    }

    public void Initialize(AppSettings settings, Func<bool> isTopPanelVisible, Action<AppSettings> apply)
    {
        _settings = settings;
        _isTopPanelVisible = isTopPanelVisible;
        _apply = apply;
        if (IsLoaded) Refresh();
    }

    private void Refresh()
    {
        _loading = true;
        RememberTopPanelCheck.IsChecked = _settings.RememberTopPanelState;
        ShortcutHint.Text = $"Повернути панелі: {ShortcutManager.Format(_shortcuts.Get(ShortcutManager.ToggleTopPanel))} або пункт «Показати адресну панель» у меню іконки в треї.";
        _loading = false;
    }

    private void RememberTopPanel_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || RememberTopPanelCheck is null) return;
        var enabled = RememberTopPanelCheck.IsChecked == true;
        _settings = AppSettingsStore.Update(settings =>
        {
            settings.RememberTopPanelState = enabled;
            settings.TopPanelHidden = enabled && _isTopPanelVisible is not null && !_isTopPanelVisible();
        });
        _apply?.Invoke(_settings);
    }

    private void Shortcuts_Changed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }
        Refresh();
    }
}
