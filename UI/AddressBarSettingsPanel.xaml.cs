using System.Windows;
using System.Windows.Controls;
using FoxBrowser.Core;

namespace FoxBrowser.UI;

public partial class AddressBarSettingsPanel : System.Windows.Controls.UserControl
{
    private readonly ShortcutManager _shortcuts = ShortcutManager.Instance;
    private bool _loading;
    private AppSettings _settings = new();

    public AddressBarSettingsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _shortcuts.Changed += Shortcuts_Changed;
            Refresh();
        };
        Unloaded += (_, _) => _shortcuts.Changed -= Shortcuts_Changed;
    }

    public AddressBarSettingsPanel(AppSettings settings) : this()
    {
        Initialize(settings);
    }

    public void Initialize(AppSettings settings) => _settings = settings;

    public event Action<AppSettings>? SettingsChanged;

    private void Refresh()
    {
        _loading = true;
        ManualRadio.IsChecked = !string.Equals(_settings.AddressBarHideMode, "Timer", StringComparison.OrdinalIgnoreCase);
        TimerRadio.IsChecked = !ManualRadio.IsChecked;
        SecondsBox.Text = Math.Clamp(_settings.AddressBarHideSeconds, 2, 60).ToString();
        SecondsBox.IsEnabled = TimerRadio.IsChecked == true;
        ShortcutHint.Text = $"Шорткат: {ShortcutManager.Format(_shortcuts.Get(ShortcutManager.ToggleAddressBar))}";
        _loading = false;
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || TimerRadio is null || TimerRadio.IsChecked != true && ManualRadio.IsChecked != true) return;
        Apply();
    }

    private void Seconds_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        Apply();
    }

    private void Apply()
    {
        var seconds = int.TryParse(SecondsBox.Text, out var parsed) ? Math.Clamp(parsed, 2, 60) : 5;
        _settings.AddressBarHideMode = TimerRadio.IsChecked == true ? "Timer" : "Manual";
        _settings.AddressBarHideSeconds = seconds;
        AppSettingsStore.Save(_settings);
        SettingsChanged?.Invoke(_settings);
    }

    private void Shortcuts_Changed(object? sender, EventArgs e) => Refresh();
}
