using System;
using System.IO;
using System.Threading.Tasks;

namespace FleetDesktop;

/// <summary>
/// Per-launch WebView2 user data folders, so cookies and cache never persist between
/// sessions (the WebView2 equivalent of WKWebView's non-persistent data store).
/// </summary>
internal static class WebViewProfiles
{
    /// <summary>
    /// Creates a fresh profile folder under %LOCALAPPDATA%\FleetDesktop\<paramref name="purpose"/>
    /// and sweeps the stale siblings earlier launches left behind.
    /// </summary>
    public static string CreateUserDataFolder(string purpose)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localAppData, "FleetDesktop", purpose);
        var userDataFolder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataFolder);

        // Earlier launches leave their per-launch folders behind (the browser
        // process holds them until it exits, so they can't be deleted on
        // shutdown). Sweep stale siblings in the background; folders still in
        // use simply fail to delete and are skipped.
        _ = Task.Run(() => CleanupStaleUserDataFolders(root, userDataFolder));
        return userDataFolder;
    }

    /// <summary>
    /// Best-effort delete of per-launch profile folders left behind by earlier runs.
    /// Folders still held open by a live browser process are left untouched; they'll
    /// be swept on a later launch.
    /// </summary>
    private static void CleanupStaleUserDataFolders(string root, string keep)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (string.Equals(dir, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // A recursive delete is not atomic — it would strip unlocked
                    // files out of a profile a live instance (same user, another
                    // session) is still using before failing on a locked one.
                    // Renaming the directory first fails outright while any file
                    // inside is open, so in-use profiles are never touched.
                    var tombstone = dir + ".stale";
                    Directory.Move(dir, tombstone);
                    Directory.Delete(tombstone, recursive: true);
                }
                catch
                {
                    // In use or already gone — skip.
                }
            }
        }
        catch
        {
            // Root missing or unreadable — nothing to sweep.
        }
    }
}
