using System;
using System.Diagnostics;
using System.IO;

namespace FleetDesktop;

/// <summary>
/// Append-only log for the <c>notify</c> command, the Windows stand-in for the
/// macOS app's unified-logging category. Lives under the running user's
/// %LOCALAPPDATA% (the SYSTEM profile when the parent runs from fleetd's script
/// runner) and rotates once at 1 MB.
///
/// Never log a notification URL: the server embeds the device token in its path.
/// </summary>
internal static class NotifyLog
{
    private const long RotateAt = 1_000_000;
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _resolved;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        System.Diagnostics.Debug.WriteLine($"Fleet Desktop notify [{level}]: {message}");
        try
        {
            lock (Gate)
            {
                if (!_resolved)
                {
                    _resolved = true;
                    _path = Resolve();
                }
                if (_path == null) return;

                var info = new FileInfo(_path);
                if (info.Exists && info.Length > RotateAt)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }
                File.AppendAllText(
                    _path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the notification down with it.
        }
    }

    private static string? Resolve()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData)) return null;
            var dir = Path.Combine(localAppData, "FleetDesktop", "Logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "notify.log");
        }
        catch
        {
            return null;
        }
    }
}
