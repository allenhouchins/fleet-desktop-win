using System;
using System.Windows.Media;
using Microsoft.Win32;
using static FleetDesktop.NativeMethods;

namespace FleetDesktop;

/// <summary>
/// Light/dark appearance. WPF on .NET 8 has no built-in dark theme, so the parts of
/// the chrome the app itself draws (title bar, loading overlay, toast card) follow
/// the "Choose your default app mode" setting by hand. Web content follows it on its
/// own through prefers-color-scheme.
/// </summary>
internal static class WindowsTheme
{
    public static readonly Color DarkWindowBackground = Color.FromRgb(0x20, 0x20, 0x20);

    /// <summary>True when Windows apps are set to dark mode.</summary>
    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Switches the DWM-drawn title bar between light and dark.</summary>
    public static void ApplyTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        try
        {
            // The attribute id moved in Windows 10 20H1; try the current one first.
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
            }
        }
        catch
        {
            // Pre-1809 builds lack the attribute; the title bar simply stays light.
        }
    }

    /// <summary>Asks DWM for rounded corners (Windows 11; a no-op elsewhere).</summary>
    public static void ApplyRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var preference = DWMWCP_ROUND;
        try
        {
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // Not supported on this build.
        }
    }
}
