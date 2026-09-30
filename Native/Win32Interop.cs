using System.Runtime.InteropServices;

namespace FoxBrowser.Native;

internal static class Win32Interop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public static void ApplyDarkTitleBar(nint hwnd, bool enabled)
    {
        if (hwnd == 0) return;
        var value = enabled ? 1 : 0;
        var result = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        if (result != 0) DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
    }
}
