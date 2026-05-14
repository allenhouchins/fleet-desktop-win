using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace FleetDesktop;

/// <summary>
/// WebView2-backed browser window. Port of BrowserWindow.swift.
///
/// Lifecycle:
///   var w = new MainWindow();
///   w.Preload(uri);   // starts loading without showing the window
///   w.Show();         // brings the window forward (loading overlay shown until page loads)
///
/// The WebView is kept alive between Show()/Hide() calls so reopening is instant.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Window title shown in the title bar.</summary>
    public const string WindowTitle = "Fleet Desktop";

    /// <summary>File extensions that should be downloaded rather than displayed.</summary>
    private static readonly HashSet<string> DownloadableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows installers / archives / docs. The macOS app also includes .mobileconfig/.pkg/.dmg —
        // we keep those too in case Fleet serves them (they'll just save to Downloads).
        "msi", "exe", "appx", "msix", "appxbundle", "msixbundle",
        "zip", "tar", "gz", "7z", "cab", "pdf",
        "mobileconfig", "pkg", "dmg",
    };

    /// <summary>MIME types that should be downloaded rather than displayed.</summary>
    private static readonly HashSet<string> DownloadableMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/octet-stream",
        "application/zip",
        "application/x-tar",
        "application/gzip",
        "application/pdf",
        "application/x-msi",
        "application/x-msdownload",
        "application/vnd.microsoft.portable-executable",
        "application/x-apple-aspen-config",
    };

    /// <summary>URL schemes safe to open externally.</summary>
    private static readonly HashSet<string> AllowedExternalSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "https", "http", "mailto",
    };

    /// <summary>Raised when a navigation error occurs (network or expired-token error page).</summary>
    public event Action? NavigationError;

    /// <summary>Raised the first time the window is shown (used to kick a token refresh).</summary>
    public event Action? WindowShown;

    private string? _fleetHost;
    private Uri? _homeUrl;
    private bool _pageLoaded;
    private bool _coreReady;
    private bool _ssoFlowActive;
    private int? _pendingStatusCode;
    private string? _pendingNavigationUri;

    public MainWindow()
    {
        InitializeComponent();
        Title = WindowTitle;
        CommandBindings.Add(new CommandBinding(NavigationCommands.Refresh, (_, _) => ReloadCurrent()));
        InputBindings.Add(new KeyBinding(NavigationCommands.Refresh, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(NavigationCommands.Refresh, Key.R, ModifierKeys.Control));
    }

    /// <summary>True once the WebView has been preloaded (window may not yet be visible).</summary>
    public bool IsAvailable => _coreReady;

    /// <summary>Start loading the URL in the WebView without showing the window.</summary>
    public void Preload(Uri url)
    {
        _fleetHost = url.Host;
        _homeUrl = url;
        _ = InitializeWebViewAsync(url);
    }

    /// <summary>Reload whatever page is currently shown.</summary>
    public void ReloadCurrent()
    {
        if (_coreReady)
        {
            WebView.CoreWebView2.Reload();
        }
    }

    /// <summary>Navigate the WebView to a new URL (e.g. after token refresh).</summary>
    public void Reload(Uri url)
    {
        _fleetHost = url.Host;
        _homeUrl = url;
        if (_coreReady)
        {
            WebView.CoreWebView2.Navigate(url.ToString());
        }
        else
        {
            // Will be loaded once the core finishes initializing.
            WebView.Source = url;
        }
    }

    /// <summary>Update the taskbar overlay icon based on the failing-policy count.</summary>
    public void SetBadge(int failingPoliciesCount)
    {
        if (TaskbarItemInfo == null)
        {
            TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();
        }
        if (failingPoliciesCount <= 0)
        {
            TaskbarItemInfo.Overlay = null;
            TaskbarItemInfo.Description = WindowTitle;
        }
        else
        {
            TaskbarItemInfo.Overlay = TaskbarBadge.Create(failingPoliciesCount);
            TaskbarItemInfo.Description = failingPoliciesCount == 1
                ? "1 failing policy"
                : $"{failingPoliciesCount} failing policies";
        }
    }

    // ---- WebView2 init -------------------------------------------------------

    private async Task InitializeWebViewAsync(Uri url)
    {
        try
        {
            // Per-launch user data folder under %LOCALAPPDATA% so cookies/cache
            // don't persist between sessions (mirrors WKWebView .nonPersistent()).
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userDataFolder = Path.Combine(localAppData, "FleetDesktop", "WebView2", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder).ConfigureAwait(true);
            await WebView.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            var core = WebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsScriptEnabled = true;
            core.Settings.IsBuiltInErrorPageEnabled = true;

            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebResourceResponseReceived += OnWebResourceResponseReceived;
            core.DownloadStarting += OnDownloadStarting;

            _coreReady = true;
            core.Navigate(url.ToString());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Fleet Desktop: WebView2 init failed: {ex}");
            MessageBox.Show(
                "Fleet Desktop requires the Microsoft Edge WebView2 Runtime to be installed.\n\n" +
                "If this Windows install is older than Windows 11, please install the WebView2 Evergreen Runtime " +
                "from https://developer.microsoft.com/microsoft-edge/webview2/ and try again.",
                WindowTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Application.Current.Shutdown(2);
        }
    }

    // ---- Navigation handlers -------------------------------------------------

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Track the top-level navigation target so OnWebResourceResponseReceived can match
        // the main-document response (WebView2 doesn't surface ResourceContext on that event).
        _pendingNavigationUri = e.Uri;

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        var scheme = uri.Scheme?.ToLowerInvariant() ?? "";

        // Same-host navigations: always allow.
        if (string.Equals(uri.Host, _fleetHost, StringComparison.OrdinalIgnoreCase)) return;
        if (scheme == "about" || scheme == "data") return;

        // SSO flow: allow HTTPS to external hosts so the IdP redirect chain completes in-app.
        if (_ssoFlowActive)
        {
            if (scheme == "https") return;
            _ssoFlowActive = false;
            e.Cancel = true;
            return;
        }

        // Heuristic: if Fleet redirected us off-host via HTTPS, treat as SSO and keep in-app.
        // (WebView2 doesn't expose navigationType/sourceFrame as cleanly as WKWebView; we rely
        // on _fleetHost being set + scheme being https.)
        if (scheme == "https" && !e.IsUserInitiated)
        {
            _ssoFlowActive = true;
            return;
        }

        // External link: open in default browser if safe, then cancel in-app navigation.
        e.Cancel = true;
        OpenExternal(uri);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var status = _pendingStatusCode;
        _pendingStatusCode = null;

        if (!e.IsSuccess)
        {
            _ssoFlowActive = false;
            NavigationError?.Invoke();
            return;
        }

        if (status is 401 or 403)
        {
            _ssoFlowActive = false;
            NavigationError?.Invoke();
            return;
        }

        _pageLoaded = true;
        Title = WindowTitle;
        if (LoadingOverlay.Visibility == Visibility.Visible)
        {
            await FadeOutAsync(LoadingOverlay, TimeSpan.FromMilliseconds(250));
        }

        if (_ssoFlowActive && string.Equals(WebView.CoreWebView2.Source != null
                ? new Uri(WebView.CoreWebView2.Source).Host
                : null,
                _fleetHost,
                StringComparison.OrdinalIgnoreCase))
        {
            _ssoFlowActive = false;
        }

        await CheckPageForErrorsAsync();
    }

    /// <summary>
    /// Captures HTTP status codes for the top-level document so we can detect 401/403 on token expiry.
    /// </summary>
    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            if (e.Request?.Method != "GET") return;
            var uri = e.Request?.Uri;
            if (string.IsNullOrEmpty(uri)) return;
            // Filter to the main document: match the URI against the pending top-level navigation.
            // Subresources (CSS/JS/images) have different URIs so they're filtered out automatically.
            if (!string.Equals(uri, _pendingNavigationUri, StringComparison.Ordinal)) return;
            _pendingStatusCode = e.Response?.StatusCode;
        }
        catch
        {
            // Ignore malformed events.
        }
    }

    /// <summary>
    /// Fleet returns HTTP 200 with error HTML when a token expires. Check for a combination
    /// of error strings (2 of 3) to reduce false positives.
    /// </summary>
    private async Task CheckPageForErrorsAsync()
    {
        const string js = @"
            (function() {
                var body = document.body ? document.body.innerText : '';
                var errors = 0;
                if (body.indexOf('Something went wrong') !== -1) errors++;
                if (body.indexOf('Error loading software') !== -1) errors++;
                if (body.indexOf('Please contact your IT admin') !== -1) errors++;
                return errors >= 2 ? 'error' : 'ok';
            })();
        ";
        try
        {
            var result = await WebView.CoreWebView2.ExecuteScriptAsync(js);
            if (result != null && result.Contains("\"error\"", StringComparison.Ordinal))
            {
                NavigationError?.Invoke();
            }
        }
        catch
        {
            // Ignore — best-effort detection.
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // target="_blank" / window.open: route same-host to current view, external to default browser.
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (string.Equals(uri.Host, _fleetHost, StringComparison.OrdinalIgnoreCase) || _ssoFlowActive)
        {
            WebView.CoreWebView2.Navigate(uri.ToString());
        }
        else
        {
            OpenExternal(uri);
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            var url = e.DownloadOperation?.Uri ?? "";
            var ext = "";
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                ext = Path.GetExtension(parsed.AbsolutePath).TrimStart('.').ToLowerInvariant();
            }
            var mime = e.DownloadOperation?.MimeType ?? "";

            // If neither extension nor MIME type matches our allowlist, let WebView2 use its default
            // behavior (which is to download). We don't cancel — we just rehome the file under ~/Downloads
            // with a deduplicated name.
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (!Directory.Exists(downloads))
            {
                downloads = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            var fileName = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = "download";
            }
            var dest = Path.Combine(downloads, fileName);
            var counter = 1;
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var fileExt = Path.GetExtension(fileName);
            while (File.Exists(dest) && counter < 1000)
            {
                dest = Path.Combine(downloads,
                    string.IsNullOrEmpty(fileExt)
                        ? $"{baseName} ({counter})"
                        : $"{baseName} ({counter}){fileExt}");
                counter++;
            }
            e.ResultFilePath = dest;

            _ = ext; _ = mime; // Reserved for future per-type handling.
        }
        catch
        {
            // Fall back to WebView2's default download behavior.
        }
    }

    // ---- External URL handling ----------------------------------------------

    private static void OpenExternal(Uri uri)
    {
        var scheme = uri.Scheme?.ToLowerInvariant() ?? "";
        if (!AllowedExternalSchemes.Contains(scheme)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort; if the OS can't open the URL, we don't bubble up.
        }
    }

    // ---- Show/Close hooks ----------------------------------------------------

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Hide instead of close so the WebView stays alive and reopening is instant.
        e.Cancel = true;
        Hide();
    }

    public new void Show()
    {
        if (Visibility != Visibility.Visible)
        {
            base.Show();
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Brief Topmost flicker forces the window to the foreground — common WPF pattern
        // for bringing a hidden window forward without leaving it pinned on top.
        Topmost = true;
        Topmost = false;
        Focus();

        WindowShown?.Invoke();
    }

    private static async Task FadeOutAsync(UIElement element, TimeSpan duration)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = duration,
        };
        var tcs = new TaskCompletionSource<bool>();
        anim.Completed += (_, _) =>
        {
            element.Visibility = Visibility.Collapsed;
            element.Opacity = 1.0;
            tcs.TrySetResult(true);
        };
        element.BeginAnimation(UIElement.OpacityProperty, anim);
        await tcs.Task;
    }
}
