using System.Runtime.InteropServices;

namespace FoxBrowser.Native;

internal static class Win32Interop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

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
