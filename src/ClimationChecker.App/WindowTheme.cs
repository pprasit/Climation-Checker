using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClimationChecker.App;

internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20h1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void ApplyDarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20h1, ref enabled, sizeof(int));

        var captionColor = ToColorRef(5, 5, 5);
        var borderColor = ToColorRef(48, 48, 48);
        var textColor = ToColorRef(228, 228, 228);
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref borderColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(int));
    }

    private static int ToColorRef(byte red, byte green, byte blue)
    {
        return red | (green << 8) | (blue << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
