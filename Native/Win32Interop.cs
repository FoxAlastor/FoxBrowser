using System.Runtime.InteropServices;

namespace FoxBrowser.Native;

internal static class Win32Interop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaCloak = 13;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int GclpHbrBackground = -10;

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint colorRef);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern nint SetClassLongPtr(nint hwnd, int index, nint value);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private const int GwlStyle = -16;
    private const nint WsBorder = 0x00800000;
    private const nint WsDlgFrame = 0x00400000;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    public static void ApplyDarkTitleBar(nint hwnd, bool enabled)
    {
        if (hwnd == 0) return;
        var value = enabled ? 1 : 0;
        var result = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        if (result != 0) DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
    }

    /// <summary>Фарбує фон класу вікна, щоб до першого кадру WPF не було світлого прямокутника.</summary>
    public static void SetWindowClassBackground(nint hwnd, byte r, byte g, byte b)
    {
        if (hwnd == 0) return;
        try
        {
            var brush = CreateSolidBrush((uint)(r | (g << 8) | (b << 16)));
            if (brush != 0) SetClassLongPtr(hwnd, GclpHbrBackground, brush);
        }
        catch { }
    }

    public static void SetWindowCloaked(nint hwnd, bool cloaked)
    {
        if (hwnd == 0) return;
        try
        {
            var value = cloaked ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int));
        }
        catch { }
    }

    public static void SetWindowCaptionColor(nint hwnd, byte r, byte g, byte b, byte textR, byte textG, byte textB)
    {
        if (hwnd == 0) return;
        try
        {
            var captionColor = (int)(r | (g << 8) | (b << 16));
            var textColor = (int)(textR | (textG << 8) | (textB << 16));
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColor, sizeof(int));
        }
        catch { }
    }

    public static void RemovePopupFrame(nint hwnd)
    {
        if (hwnd == 0) return;
        var style = GetWindowLongPtr(hwnd, GwlStyle);
        var withoutFrame = style & ~WsBorder & ~WsDlgFrame;
        if (style == withoutFrame) return;
        SetWindowLongPtr(hwnd, GwlStyle, withoutFrame);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
    }
}
