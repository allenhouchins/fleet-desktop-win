using System;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;

namespace FleetDesktop;

/// <summary>
/// Remembers the main window's position and size between launches, the Windows
/// equivalent of the macOS app's frame autosave. Stored per user in HKCU.
/// </summary>
internal static class WindowPlacement
{
    private const string KeyPath = @"Software\FleetDM\FleetDesktop\Window";

    public static void Restore(Window window)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key == null) return;
            if (!TryRead(key, "Left", out var left) || !TryRead(key, "Top", out var top) ||
                !TryRead(key, "Width", out var width) || !TryRead(key, "Height", out var height))
            {
                return;
            }

            width = Math.Max(width, window.MinWidth);
            height = Math.Max(height, window.MinHeight);

            // Only restore onto a display that still exists — a window saved on a
            // detached external monitor would otherwise come back off-screen.
            var saved = new Rect(left, top, width, height);
            var screen = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var visible = Rect.Intersect(saved, screen);
            if (visible.IsEmpty || visible.Width < 100 || visible.Height < 100) return;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
            window.Width = width;
            window.Height = height;
        }
        catch
        {
            // Fall back to the default centered placement.
        }
    }

    public static void Save(Window window)
    {
        try
        {
            // RestoreBounds holds the normal-state rect even while maximized/minimized.
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                : window.RestoreBounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;

            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (key == null) return;
            key.SetValue("Left", bounds.Left.ToString(CultureInfo.InvariantCulture));
            key.SetValue("Top", bounds.Top.ToString(CultureInfo.InvariantCulture));
            key.SetValue("Width", bounds.Width.ToString(CultureInfo.InvariantCulture));
            key.SetValue("Height", bounds.Height.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // Best-effort.
        }
    }

    private static bool TryRead(RegistryKey key, string name, out double value)
    {
        value = 0;
        return key.GetValue(name) is string s &&
               double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }
}
