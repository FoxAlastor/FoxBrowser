using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using FoxBrowser.Core;

namespace FoxBrowser.UI;

public partial class AppSettingsWindow : Window
{
    private readonly Action<AppSettings> _apply;
    private string? _iconPath;

    public AppSettingsWindow(AppSettings current, Action<AppSettings> apply)
    {
        InitializeComponent();
        _apply = apply;
        NameBox.Text = current.AppDisplayName;
        _iconPath = current.AppIconPath;
        UpdateIconPathText();
    }

    private void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Оберіть іконку застосунку",
            Filter = "Зображення та іконки (*.ico;*.png;*.jpg;*.jpeg)|*.ico;*.png;*.jpg;*.jpeg|Усі файли (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _iconPath = AppIconConverter.CopyAsPortableIcon(dialog.FileName);
            UpdateIconPathText();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Не вдалося підготувати іконку: {ex.Message}", "Іконка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = "FoxBrowser";
        try
        {
            var settings = new AppSettings
            {
                AppDisplayName = name,
                AppIconPath = _iconPath,
                Shortcuts = ShortcutManager.Instance.Shortcuts.ToDictionary(pair => pair.Key, pair => ShortcutManager.Format(pair.Value))
            };
            AppSettingsStore.Save(settings);
            _apply(settings);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Не вдалося зберегти налаштування: {ex.Message}", "Налаштування", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        NameBox.Text = "FoxBrowser";
        _iconPath = null;
        try { File.Delete(Path.Combine(AppContext.BaseDirectory, "custom_icon.ico")); } catch { }
        UpdateIconPathText();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateIconPathText() => IconPathText.Text = string.IsNullOrWhiteSpace(_iconPath) ? "Вбудована іконка" : Path.GetFileName(_iconPath);
}

internal static class AppIconConverter
{
    private const string PortableIconName = "custom_icon.ico";

    public static string CopyAsPortableIcon(string sourcePath)
    {
        var destination = Path.Combine(AppContext.BaseDirectory, PortableIconName);
        var extension = Path.GetExtension(sourcePath);
        if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                return destination;
            File.Copy(sourcePath, destination, overwrite: true);
            return destination;
        }

        using var bitmap = new Bitmap(sourcePath);
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            using var stream = File.Create(destination);
            icon.Save(stream);
        }
        finally
        {
            DestroyIcon(handle);
        }
        return destination;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}
