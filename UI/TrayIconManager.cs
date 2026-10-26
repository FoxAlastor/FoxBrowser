using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using FoxBrowser.Native;
using Forms = System.Windows.Forms;

namespace FoxBrowser.UI;

public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _window;
    private readonly Action _exit;
    private readonly Action _showAddressBar;
    private Icon? _ownedIcon;
    private WindowState _restoreState = WindowState.Normal;

    public TrayIconManager(Window window, Action exit, Action showAddressBar)
    {
        _window = window;
        _restoreState = window.WindowState == WindowState.Minimized
            ? WindowState.Normal
            : window.WindowState;
        _exit = exit;
        _showAddressBar = showAddressBar;
        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState != WindowState.Minimized)
                _restoreState = _window.WindowState;
        };

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
        var hwnd = new WindowInteropHelper(_window).Handle;
        Win32Interop.SetWindowCloaked(hwnd, true);
        _window.Show();
        _window.WindowState = _restoreState;
        _window.Activate();
        _window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            Win32Interop.SetWindowCloaked(hwnd, false);
        }));
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
        var menu = new Forms.ContextMenuStrip
        {
            BackColor = System.Drawing.Color.FromArgb(23, 27, 34),
            ForeColor = System.Drawing.Color.FromArgb(245, 247, 251),
            Font = new System.Drawing.Font("Segoe UI", 9F),
            Renderer = new DarkTrayMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new System.Windows.Forms.Padding(6, 3, 6, 3)
        };
        menu.Items.Add("Показати / сховати", null, (_, _) => ToggleWindow());
        menu.Items.Add("Показати адресну панель", null, (_, _) => _showAddressBar());
        menu.Items.Add("Вийти", null, (_, _) => _exit());
        foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
        {
            item.AutoSize = false;
            item.Height = 30;
            item.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            item.Padding = new System.Windows.Forms.Padding(10, 0, 10, 0);
        }
        return menu;
    }

    private sealed class DarkTrayMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        public DarkTrayMenuRenderer() : base(new DarkTrayColorTable()) { }

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            var textBounds = e.Item.ContentRectangle;
            textBounds.X += 10;
            textBounds.Width = Math.Max(0, textBounds.Width - 20);
            System.Windows.Forms.TextRenderer.DrawText(
                e.Graphics,
                e.Text,
                e.TextFont,
                textBounds,
                System.Drawing.Color.FromArgb(245, 247, 251),
                System.Windows.Forms.TextFormatFlags.Left |
                System.Windows.Forms.TextFormatFlags.VerticalCenter |
                System.Windows.Forms.TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderMenuItemBackground(System.Windows.Forms.ToolStripItemRenderEventArgs e)
        {
            var color = e.Item.Selected
                ? System.Drawing.Color.FromArgb(40, 48, 60)
                : System.Drawing.Color.FromArgb(23, 27, 34);
            using var brush = new System.Drawing.SolidBrush(color);
            e.Graphics.FillRectangle(brush, new System.Drawing.Rectangle(System.Drawing.Point.Empty, e.Item.Size));
        }
    }

    private sealed class DarkTrayColorTable : System.Windows.Forms.ProfessionalColorTable
    {
        private static readonly System.Drawing.Color Background = System.Drawing.Color.FromArgb(23, 27, 34);
        private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(40, 48, 60);
        private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(51, 64, 78);

        public override System.Drawing.Color MenuBorder => Border;
        public override System.Drawing.Color MenuItemBorder => Border;
        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override System.Drawing.Color ToolStripDropDownBackground => Background;
        public override System.Drawing.Color ImageMarginGradientBegin => Background;
        public override System.Drawing.Color ImageMarginGradientMiddle => Background;
        public override System.Drawing.Color ImageMarginGradientEnd => Background;
        public override System.Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemPressedGradientMiddle => Hover;
        public override System.Drawing.Color MenuItemPressedGradientEnd => Hover;
    }
}
