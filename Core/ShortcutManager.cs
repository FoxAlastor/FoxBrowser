using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace FoxBrowser.Core;

public sealed class ShortcutManager
{
    public const string CloseTab = "CloseTab";
    public const string NewTab = "NewTab";
    public const string ToggleTopPanel = "ToggleTopPanel";
    public const string ToggleAddressBar = "ToggleAddressBar";

    public static IReadOnlyList<string> AllActions { get; } = new[] { CloseTab, NewTab, ToggleTopPanel, ToggleAddressBar };

    private static readonly IReadOnlyDictionary<string, KeyGesture> Defaults =
        new Dictionary<string, KeyGesture>
        {
            [CloseTab] = new(Key.W, ModifierKeys.Control),
            [NewTab] = new(Key.N, ModifierKeys.Control),
            [ToggleTopPanel] = new(Key.F, ModifierKeys.Control | ModifierKeys.Shift),
            [ToggleAddressBar] = new(Key.A, ModifierKeys.Control | ModifierKeys.Shift),
        };

    private static readonly Lazy<ShortcutManager> LazyInstance = new(() => new ShortcutManager());
    private readonly Dictionary<string, KeyGesture> _shortcuts = new();

    private ShortcutManager()
    {
        ResetToDefaults(save: false);
        Load(AppSettingsStore.Load());
    }

    public static ShortcutManager Instance => LazyInstance.Value;
    public IReadOnlyDictionary<string, KeyGesture> Shortcuts => _shortcuts;
    public event EventHandler? Changed;

    public KeyGesture Get(string action) => _shortcuts[action];

    public string? FindAction(Key key, ModifierKeys modifiers)
    {
        return _shortcuts.FirstOrDefault(pair => pair.Value.Key == key && pair.Value.Modifiers == modifiers).Key;
    }

    public bool TrySet(string action, KeyGesture gesture, out string? conflictAction)
    {
        conflictAction = _shortcuts.FirstOrDefault(pair => pair.Key != action && GesturesEqual(pair.Value, gesture)).Key;
        if (!string.IsNullOrEmpty(conflictAction)) return false;
        _shortcuts[action] = gesture;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ResetToDefaults()
    {
        ResetToDefaults(save: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string GetActionLabel(string action) => action switch
    {
        CloseTab => "Закрити вкладку",
        NewTab => "Нова вкладка",
        ToggleTopPanel => "Показати/сховати верхню панель",
        ToggleAddressBar => "Показати/сховати адресну панель",
        _ => action,
    };

    public static string Format(KeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(gesture.Key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryCreateGesture(Key key, ModifierKeys modifiers, out KeyGesture? gesture)
    {
        gesture = null;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return false;
        if (key == Key.None || modifiers == ModifierKeys.None)
            return false;
        gesture = new KeyGesture(key, modifiers);
        return true;
    }

    private void Load(AppSettings settings)
    {
        if (settings.Shortcuts is null) return;
        foreach (var pair in settings.Shortcuts)
        {
            if (!Defaults.ContainsKey(pair.Key)) continue;
            if (TryParse(pair.Value, out var gesture) &&
                !_shortcuts.Any(existing => !string.Equals(existing.Key, pair.Key, StringComparison.Ordinal) && GesturesEqual(existing.Value, gesture!)))
                _shortcuts[pair.Key] = gesture!;
        }
    }

    private void Save()
    {
        var settings = AppSettingsStore.Load();
        settings.Shortcuts = _shortcuts.ToDictionary(pair => pair.Key, pair => Format(pair.Value));
        AppSettingsStore.Save(settings);
    }

    private void ResetToDefaults(bool save)
    {
        _shortcuts.Clear();
        foreach (var pair in Defaults) _shortcuts[pair.Key] = pair.Value;
        if (save) Save();
    }

    private static bool GesturesEqual(KeyGesture left, KeyGesture right) => left.Key == right.Key && left.Modifiers == right.Modifiers;

    private static bool TryParse(string? value, out KeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !Enum.TryParse(parts[^1], true, out Key key)) return false;
        var modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Control;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Shift;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Alt;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierKeys.Windows;
            else return false;
        }
        if (!TryCreateGesture(key, modifiers, out gesture)) return false;
        return true;
    }
}
