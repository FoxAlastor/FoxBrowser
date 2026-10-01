using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace FoxBrowser.UI;

public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _window;
    private readonly Action _exit;
    private Icon? _ownedIcon;

    public TrayIconManager(Window window, Action exit)
    {
        _window = window;
        _exit = exit;

        _icon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = window.Title,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _icon.MouseClick += OnMouseClick;
        UpdateIdentity(window.Title, System.IO.Path.Combine(AppContext.BaseDirectory, "FoxBrowser.ico"));
    }

    public void MinimizeToTray()
    {
        _window.ShowInTaskbar = false;
        _window.Hide();
    }

    public void ToggleWindow()
    {
        if (_window.IsVisible && _window.WindowState != WindowState.Minimized)
        {
            MinimizeToTray();
            return;
        }

        _window.ShowInTaskbar = true;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        _icon.MouseClick -= OnMouseClick;
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon?.Dispose();
    }

    public void UpdateIdentity(string displayName, string iconPath)
    {
        try
        {
            Icon? next = null;
            if (System.IO.File.Exists(iconPath)) next = new Icon(iconPath);
            var old = _ownedIcon;
            _ownedIcon = next;
            _icon.Icon = next ?? SystemIcons.Application;
            old?.Dispose();
        }
        catch { }

        _icon.Text = string.IsNullOrWhiteSpace(displayName) ? "FoxBrowser" : displayName[..Math.Min(63, displayName.Length)];
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) ToggleWindow();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Показати / сховати", null, (_, _) => ToggleWindow());
        menu.Items.Add("Вийти", null, (_, _) => _exit());
        return menu;
    }
}
