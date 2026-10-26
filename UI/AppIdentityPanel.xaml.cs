using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using FoxBrowser.Core;

namespace FoxBrowser.UI;

public partial class AppIdentityPanel : System.Windows.Controls.UserControl
{
    private Action<AppSettings>? _apply;
    private string? _iconPath;

    public AppIdentityPanel()
    {
        InitializeComponent();
    }

    public AppIdentityPanel(AppSettings current, Action<AppSettings> apply) : this()
    {
        Initialize(current, apply);
    }

    public void Initialize(AppSettings current, Action<AppSettings> apply)
    {
        _apply = apply;
        NameBox.Text = current.AppDisplayName;
        _iconPath = current.AppIconPath;
        UpdateIconPathText();
    }

    public event EventHandler? Applied;

    private void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Оберіть іконку застосунку",
            Filter = "Зображення та іконки (*.ico;*.png;*.jpg;*.jpeg)|*.ico;*.png;*.jpg;*.jpeg|Усі файли (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            _iconPath = AppIconConverter.CopyAsPortableIcon(dialog.FileName);
            UpdateIconPathText();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), $"Не вдалося підготувати іконку: {ex.Message}", "Іконка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettingsStore.Update(current =>
        {
            current.AppDisplayName = string.IsNullOrWhiteSpace(NameBox.Text) ? "FoxBrowser" : NameBox.Text.Trim();
            current.AppIconPath = _iconPath;
        });
        _apply?.Invoke(settings);
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        NameBox.Text = "FoxBrowser";
        _iconPath = null;
        try { File.Delete(Path.Combine(AppContext.BaseDirectory, "custom_icon.ico")); } catch { }
        UpdateIconPathText();
    }

    private void UpdateIconPathText() => IconPathText.Text = string.IsNullOrWhiteSpace(_iconPath) ? "Вбудована іконка" : Path.GetFileName(_iconPath);
}

internal static class AppIconConverter
{
    private const string PortableIconName = "custom_icon.ico";

    public static string CopyAsPortableIcon(string sourcePath)
    {
        var destination = Path.Combine(AppContext.BaseDirectory, PortableIconName);
        if (Path.GetExtension(sourcePath).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
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
        finally { DestroyIcon(handle); }
        return destination;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);
}
