using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FoxBrowser.Core;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace FoxBrowser.UI;

public partial class ShortcutSettingsWindow : Window
{
    private readonly ShortcutManager _shortcuts = ShortcutManager.Instance;
    private readonly Dictionary<string, WpfTextBox> _boxes = new();
    private string? _capturingAction;
    private KeyGesture? _pendingGesture;

    public ShortcutSettingsWindow()
    {
        InitializeComponent();
        _shortcuts.Changed += Shortcuts_Changed;
        BuildRows();
    }

    private void BuildRows()
    {
        ShortcutList.Children.Clear();
        _boxes.Clear();
        foreach (var action in new[] { ShortcutManager.CloseTab, ShortcutManager.NewTab, ShortcutManager.ToggleTopPanel })
        {
            var box = new WpfTextBox
            {
                Text = ShortcutManager.Format(_shortcuts.Get(action)),
                IsReadOnly = true,
                IsTabStop = false,
                Width = 150,
                Padding = new Thickness(7, 4, 7, 4),
                Background = new SolidColorBrush(WpfColor.FromRgb(32, 35, 40)),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(242, 244, 247)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(69, 76, 87)),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
                Tag = action,
            };
            var change = new WpfButton { Content = "Змінити", Padding = new Thickness(10, 5, 10, 5), Tag = action };
            change.Click += Change_Click;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = ShortcutManager.GetActionLabel(action), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(box, 1);
            Grid.SetColumn(change, 2);
            change.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(box);
            row.Children.Add(change);
            ShortcutList.Children.Add(row);
            _boxes[action] = box;
        }
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button || button.Tag is not string action || !_boxes.TryGetValue(action, out var box)) return;
        _capturingAction = action;
        _pendingGesture = null;
        box.Text = "Натисніть комбінацію…";
        box.Focus();
        box.PreviewKeyDown -= CaptureBox_PreviewKeyDown;
        box.PreviewKeyDown += CaptureBox_PreviewKeyDown;
        box.PreviewKeyUp -= CaptureBox_PreviewKeyUp;
        box.PreviewKeyUp += CaptureBox_PreviewKeyUp;
    }

    private void CaptureBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_capturingAction is null || sender is not WpfTextBox box) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelCapture(box);
            e.Handled = true;
            return;
        }
        var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt);
        if (!ShortcutManager.TryCreateGesture(key, modifiers, out var gesture)) return;
        _pendingGesture = gesture;
        box.Text = ShortcutManager.Format(gesture);
        e.Handled = true;
    }

    private void CaptureBox_PreviewKeyUp(object sender, WpfKeyEventArgs e)
    {
        if (_capturingAction is null || _pendingGesture is null || sender is not WpfTextBox box) return;
        var action = _capturingAction;
        if (!_shortcuts.TrySet(action, _pendingGesture, out var conflictAction))
        {
            System.Windows.MessageBox.Show(this, $"Комбінація вже призначена для «{ShortcutManager.GetActionLabel(conflictAction!)}». Оберіть іншу.", "Конфлікт шорткатів", MessageBoxButton.OK, MessageBoxImage.Warning);
            box.Text = ShortcutManager.Format(_shortcuts.Get(action));
        }
        StopCapture(box);
    }

    private void CancelCapture(WpfTextBox box)
    {
        box.Text = ShortcutManager.Format(_shortcuts.Get(_capturingAction!));
        StopCapture(box);
    }

    private void StopCapture(WpfTextBox box)
    {
        box.PreviewKeyDown -= CaptureBox_PreviewKeyDown;
        box.PreviewKeyUp -= CaptureBox_PreviewKeyUp;
        _capturingAction = null;
        _pendingGesture = null;
        Keyboard.ClearFocus();
    }

    private void Shortcuts_Changed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(BuildRows);
            return;
        }
        BuildRows();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => _shortcuts.ResetToDefaults();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _shortcuts.Changed -= Shortcuts_Changed;
        base.OnClosed(e);
    }
}
