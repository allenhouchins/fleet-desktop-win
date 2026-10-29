using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace FleetDesktop;

/// <summary>
/// WebView2-backed browser window. Port of BrowserWindow.swift.
///
/// Lifecycle:
///   var w = new MainWindow();
///   w.Preload(uri);   // starts loading without showing the window
///   w.Show();         // brings the window forward (loading overlay shown until page loads)
///
/// The WebView is kept alive between Show()/Hide() calls so reopening is instant;
/// while hidden it is suspended so an idle Fleet Desktop costs no CPU and little memory.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Window title shown in the title bar.</summary>
    public const string WindowTitle = "Fleet Desktop";

    /// <summary>URL schemes safe to open externally.</summary>
    private static readonly HashSet<string> AllowedExternalSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "https", "http", "mailto",
    };

    /// <summary>
    /// URL schemes of native authenticator apps an IdP launches mid-flow. Okta Verify
    /// registers <c>com-okta-authenticator</c>; Okta's sign-in page navigates to it
    /// (from a hidden iframe) for FastPass when its localhost loopback probe fails,
    /// then polls Okta for the app's answer.
    /// </summary>
    private static readonly HashSet<string> AuthenticatorSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "com-okta-authenticator",
    };

    /// <summary>Follows no redirects: used to read the transparency endpoint's
    /// <c>Location</c> header instead of following it.</summary>
    private static readonly HttpClient NoRedirectHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>How long after the window is hidden the WebView is put to sleep. Long
    /// enough that a close-and-reopen doesn't pay a resume.</summary>
    private static readonly TimeSpan SuspendDelay = TimeSpan.FromSeconds(5);

    /// <summary>Raised when a navigation error occurs (network or expired-token error page).</summary>
    public event Action? NavigationError;

    /// <summary>Raised each time the window is shown (used to kick a token refresh).</summary>
    public event Action? WindowShown;

    private string? _fleetHost;
    private Uri? _homeUrl;
    private bool _coreReady;
    private int? _pendingStatusCode;
    private string? _pendingNavigationUri;
    private readonly DispatcherTimer _suspendTimer;

    /// <summary>JavaScript to run the next time a Fleet-host page finishes loading. Consumed once.
    /// Used by fleet://update_all and fleet://install_all to click in-page buttons.</summary>
    private string? _pendingPostLoadJs;

    /// <summary>
    /// Host of the external IdP page an SSO/auth flow is currently on. Non-null
    /// while a flow is in progress; external redirects are kept in the WebView so
    /// the full redirect chain completes in-app, but navigation is restricted to
    /// this host (hops to a new host are only allowed via server redirects or
    /// scripted navigations such as auto-submitted SAML forms).
    /// </summary>
    private string? _ssoHost;

    /// <summary>When the current SSO flow started (UTC). Flows expire after
    /// <see cref="SsoFlowTimeout"/> so the chrome-less WebView can't render
    /// external sites indefinitely.</summary>
    private DateTime? _ssoFlowStartedAt;

    /// <summary>How long an SSO flow may run before external navigation is cut off.</summary>
    private static readonly TimeSpan SsoFlowTimeout = TimeSpan.FromMinutes(10);

    private bool SsoFlowActive => _ssoHost != null;

    private bool SsoFlowExpired =>
        _ssoFlowStartedAt is { } started && DateTime.UtcNow - started > SsoFlowTimeout;

    /// <summary>Resets SSO state. Called on window hide, navigation errors, and
    /// when navigation returns to the Fleet host from an SSO flow.</summary>
    private void ResetSsoFlow()
    {
        _ssoHost = null;
        _ssoFlowStartedAt = null;
    }

    /// <summary>Case-insensitive check against the Fleet server host. All
    /// navigation-policy decisions go through this one comparison.</summary>
    private bool IsFleetHost(string? host) =>
        host != null && string.Equals(host, _fleetHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Case-insensitive check against the pinned SSO/IdP host.</summary>
    private bool IsCurrentSsoHost(string? host) =>
        host != null && string.Equals(host, _ssoHost, StringComparison.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        Title = WindowTitle;
        CommandBindings.Add(new CommandBinding(NavigationCommands.Refresh, (_, _) => ReloadCurrent()));
        InputBindings.Add(new KeyBinding(NavigationCommands.Refresh, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(NavigationCommands.Refresh, Key.R, ModifierKeys.Control));

        _suspendTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SuspendDelay };
        _suspendTimer.Tick += (_, _) =>
        {
            _suspendTimer.Stop();
            _ = SuspendIfHiddenAsync();
        };

        WindowPlacement.Restore(this);
        ApplyTheme(WindowsTheme.IsDarkMode());
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Start loading the URL in the WebView without showing the window.</summary>
    public void Preload(Uri url)
    {
        _fleetHost = url.Host;
        _homeUrl = url;
        // WebView2 needs a parent HWND before it can initialize. Creating the handle
        // now (without showing the window) is what lets a hidden launch preload the
        // page, so the first Show is instant.
        new WindowInteropHelper(this).EnsureHandle();
        _ = InitializeWebViewAsync();
    }

    /// <summary>Reload whatever page is currently shown.</summary>
    public void ReloadCurrent()
    {
        if (_coreReady)
        {
            EnsureResumed();
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
            EnsureResumed();
            WebView.CoreWebView2.Navigate(url.AbsoluteUri);
        }
        // else: InitializeWebViewAsync navigates to the latest _homeUrl once the
        // core is up, so a Reload during initialization is not lost.
    }

    /// <summary>
    /// Queue JavaScript to run once, the next time a Fleet-host page finishes loading.
    /// Set this *before* calling <see cref="Preload"/> or <see cref="Reload"/>.
    /// </summary>
    public void RunOnNextLoad(string js)
    {
        _pendingPostLoadJs = js;
    }

    /// <summary>Update the taskbar overlay icon based on the failing-policy count.
    /// Set unconditionally each poll (like the macOS Dock badge) so the overlay
    /// survives taskbar recreation, e.g. an Explorer restart.</summary>
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

    /// <summary>Persists the window's position and size for the next launch.</summary>
    public void SavePlacement()
    {
        // Nothing to save until the window has been laid out at least once.
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            WindowPlacement.Save(this);
        }
    }

    // ---- WebView2 init -------------------------------------------------------

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // Per-launch user data folder under %LOCALAPPDATA% so cookies/cache
            // don't persist between sessions (mirrors WKWebView .nonPersistent()).
            var userDataFolder = WebViewProfiles.CreateUserDataFolder("WebView2");

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
            // Nothing listens for web messages in this window; close the channel.
            core.Settings.IsWebMessageEnabled = false;
            // The profile is throwaway — never offer to save passwords or autofill data.
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebResourceResponseReceived += OnWebResourceResponseReceived;
            core.DownloadStarting += OnDownloadStarting;

            _coreReady = true;
            // Navigate to the *current* home URL — Reload() may have replaced it
            // while the core was initializing (e.g. a fleet:// deep link that
            // arrived during startup).
            if (_homeUrl != null)
            {
                core.Navigate(_homeUrl.AbsoluteUri);
            }
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
        // the main-document response on runtimes that don't report HttpStatusCode.
        _pendingNavigationUri = e.Uri;

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }
        var scheme = uri.Scheme?.ToLowerInvariant() ?? "";

        if (IsTransparencyEndpoint(uri))
        {
            e.Cancel = true;
            OpenTransparencyPage(uri);
            return;
        }

        // Same-host navigations are always allowed — over HTTPS. The device token is
        // in the URL, so a downgrade to cleartext on the Fleet host is refused.
        if (IsFleetHost(uri.Host))
        {
            if (scheme != "https") e.Cancel = true;
            return;
        }
        if (scheme == "about") return;

        // During an active SSO flow, keep external IdP redirects in the WebView so
        // the chain completes in-app — but only over HTTPS, only while the flow is
        // fresh, and only on the current IdP host. Hops to a *new* external host
        // are allowed via server redirects or scripted navigations (multi-host IdP
        // chains, auto-submitted SAML forms); user link clicks to unrelated hosts
        // open in the default browser so the chrome-less WebView can't be steered
        // to arbitrary sites.
        if (SsoFlowActive)
        {
            if (TryLaunchAuthenticator(uri))
            {
                e.Cancel = true;
                return;
            }
            if (scheme != "https" || SsoFlowExpired)
            {
                // Flow over (expired or degraded to non-HTTPS). Don't just cancel —
                // that would strand the WebView on the IdP page; return home.
                ResetSsoFlow();
                e.Cancel = true;
                NavigateHome();
                return;
            }
            if (IsCurrentSsoHost(uri.Host)) return;
            if (e.IsRedirected || !e.IsUserInitiated)
            {
                _ssoHost = uri.Host.ToLowerInvariant();
                return;
            }
            e.Cancel = true;
            OpenExternal(uri);
            return;
        }

        // Detect SSO: the WebView is showing a Fleet page (no flow active) and
        // something other than a user link click — a server redirect or scripted
        // navigation — is sending it to an external HTTPS host. Start the SSO
        // flow there. Requiring the committed document to be the Fleet page
        // mirrors the macOS sourceFrame check: without it, a page left behind by
        // an expired flow could restart the flow forever, making the 10-minute
        // cap meaningless.
        if (scheme == "https" && (e.IsRedirected || !e.IsUserInitiated) &&
            IsFleetHost(GetCurrentHost()))
        {
            _ssoHost = uri.Host.ToLowerInvariant();
            _ssoFlowStartedAt = DateTime.UtcNow;
            return;
        }

        // External link from a Fleet page: open in default browser if safe. But if
        // the WebView is stranded on an external page with no active flow (an
        // expired or abandoned SSO), navigate home instead — otherwise every
        // scripted retry on the stranded page would pop another browser tab.
        e.Cancel = true;
        if (IsFleetHost(GetCurrentHost()))
        {
            OpenExternal(uri);
        }
        else
        {
            NavigateHome();
        }
    }

    /// <summary>Returns the WebView to the Fleet device page. Used to recover
    /// from a stranded state (expired/abandoned SSO flow on an external page).</summary>
    private void NavigateHome()
    {
        if (_coreReady && _homeUrl != null)
        {
            WebView.CoreWebView2.Navigate(_homeUrl.AbsoluteUri);
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var status = TryGetHttpStatus(e) ?? _pendingStatusCode;
        _pendingStatusCode = null;

        if (!e.IsSuccess)
        {
            // Cancelled loads are our own policy decisions (external links, refused
            // schemes), not failures.
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            ResetSsoFlow();
            NavigationError?.Invoke();
            return;
        }

        if (status is 401 or 403)
        {
            ResetSsoFlow();
            NavigationError?.Invoke();
            return;
        }

        Title = WindowTitle;
        if (LoadingOverlay.Visibility == Visibility.Visible)
        {
            await FadeOutAsync(LoadingOverlay, TimeSpan.FromMilliseconds(250));
        }

        var onFleetHost = IsFleetHost(GetCurrentHost());

        // If an SSO flow was active and we've finished loading a Fleet-host page,
        // the SSO callback is complete — reset the flow.
        if (SsoFlowActive && onFleetHost)
        {
            ResetSsoFlow();
        }

        await CheckPageForErrorsAsync();

        // Only run queued JS on Fleet-host pages — avoids injecting into IdP pages
        // during SSO redirects and avoids consuming the slot on an intermediate
        // redirect before the real target finishes loading.
        if (_pendingPostLoadJs is { } js && onFleetHost)
        {
            _pendingPostLoadJs = null;
            try
            {
                await WebView.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch
            {
                // Best-effort — the page simply won't get the click.
            }
        }
    }

    /// <summary>Main-document HTTP status, on runtimes new enough to report it (112+).</summary>
    private static int? TryGetHttpStatus(CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            var status = e.HttpStatusCode;
            return status > 0 ? status : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Host of the document currently loaded in the WebView, or null.</summary>
    private string? GetCurrentHost()
    {
        var source = _coreReady ? WebView.CoreWebView2.Source : null;
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// <summary>
    /// Captures HTTP status codes for the top-level document so we can detect 401/403 on
    /// token expiry with runtimes that predate NavigationCompleted.HttpStatusCode.
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
        var js = $$"""
            (function() {
                var text = document.body ? document.body.innerText : '';
                return {{FleetErrorPage.MatchesExpression}} ? 'error' : 'ok';
            })();
            """;
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
        // target="_blank" / window.open: the transparency link resolves natively;
        // same-host (or the current SSO host, while the flow is fresh) loads in this
        // view; an authenticator app gets its scheme; everything else goes to the
        // default browser.
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (IsTransparencyEndpoint(uri))
        {
            OpenTransparencyPage(uri);
        }
        else if (IsFleetHost(uri.Host) ||
                 (SsoFlowActive && !SsoFlowExpired && IsCurrentSsoHost(uri.Host)))
        {
            WebView.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
        else if (!TryLaunchAuthenticator(uri))
        {
            OpenExternal(uri);
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            // Rehome the file under the user's Downloads folder with a deduplicated
            // name. Only the final path component of WebView2's suggested path is
            // kept, so a server-supplied filename can't steer the file elsewhere.
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (!Directory.Exists(downloads))
            {
                downloads = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            var fileName = Path.GetFileName(e.ResultFilePath);
            if (string.IsNullOrEmpty(fileName) || fileName is "." or "..")
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
        }
        catch
        {
            // Fall back to WebView2's default download behavior.
        }
    }

    // ---- Transparency link ------------------------------------------------------

    /// <summary>
    /// Whether the URL is Fleet's device transparency endpoint
    /// (<c>&lt;prefix&gt;/api/&lt;version&gt;/fleet/device/&lt;token&gt;/transparency</c>),
    /// which redirects to the admin-configured transparency page. Loaded in the
    /// WebView, that redirect off the Fleet host looks like the start of an SSO
    /// flow, so the external page would render in-app with no way back.
    /// </summary>
    private bool IsTransparencyEndpoint(Uri uri)
    {
        if (!IsFleetHost(uri.Host)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 6) return false;
        var tail = parts[^6..];
        return tail[0] == "api" && tail[2] == "fleet" && tail[3] == "device" && tail[5] == "transparency";
    }

    /// <summary>
    /// Resolves the transparency endpoint's redirect natively and opens the
    /// destination in the default browser. Opening the endpoint itself in the
    /// browser would put the device token in its address bar and history. For the
    /// same reason only an absolute <c>Location</c> is opened: the server accepts a
    /// relative transparency URL and sends it verbatim, and resolving it against the
    /// endpoint would keep the token in the path.
    /// </summary>
    private async void OpenTransparencyPage(Uri uri)
    {
        var status = 0;
        string? location = null;
        string? error = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await NoRedirectHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            status = (int)response.StatusCode;
            location = response.Headers.Location?.OriginalString;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (status is >= 300 and <= 399 &&
            Uri.TryCreate(location, UriKind.Absolute, out var target) && !string.IsNullOrEmpty(target.Scheme))
        {
            OpenExternal(target);
            return;
        }

        // The page's links embed the token it was rendered with, so a rejection
        // means it rotated. Recover the same way as a 401/403 inside the WebView:
        // re-read the token and reload the page.
        if (status is 401 or 403)
        {
            NavigationError?.Invoke();
        }
        Debug.WriteLine($"Fleet Desktop: Transparency redirect failed (HTTP {status}): {error ?? "no absolute redirect target"}");
    }

    // ---- External URL handling ----------------------------------------------

    private static void OpenExternal(Uri uri)
    {
        var scheme = uri.Scheme?.ToLowerInvariant() ?? "";
        if (!AllowedExternalSchemes.Contains(scheme)) return;
        LaunchWithShell(uri);
    }

    /// <summary>
    /// Hands a navigation to a native authenticator app when an IdP asks for one
    /// during an SSO flow. The IdP page stays where it is and keeps polling, so the
    /// flow must stay active. Returns false when the URL isn't an authenticator
    /// scheme or no flow is running, so callers apply their usual policy.
    /// </summary>
    private bool TryLaunchAuthenticator(Uri uri)
    {
        if (!SsoFlowActive || SsoFlowExpired) return false;
        if (!AuthenticatorSchemes.Contains(uri.Scheme ?? "")) return false;
        LaunchWithShell(uri);
        return true;
    }

    private static void LaunchWithShell(Uri uri)
    {
        try
        {
            // AbsoluteUri keeps the URL percent-encoded, so the shell receives one
            // unambiguous token rather than a string with spaces or unicode in it.
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort; if the OS can't open the URL, we don't bubble up.
        }
    }

    // ---- Theme --------------------------------------------------------------------

    private void ApplyTheme(bool dark)
    {
        var background = dark
            ? new SolidColorBrush(WindowsTheme.DarkWindowBackground)
            : SystemColors.WindowBrush;
        RootGrid.Background = background;
        LoadingOverlay.Background = background;
        WindowsTheme.ApplyTitleBar(new WindowInteropHelper(this).Handle, dark);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Dispatcher.BeginInvoke(() => ApplyTheme(WindowsTheme.IsDarkMode()));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTheme.ApplyTitleBar(new WindowInteropHelper(this).Handle, WindowsTheme.IsDarkMode());
    }

    // ---- Suspend while hidden ---------------------------------------------------

    /// <summary>
    /// Freezes the renderer while the window is hidden, so a Fleet Desktop that was
    /// closed hours ago isn't still running page timers. WebView2 resumes on its own
    /// when the view becomes visible; navigations and scripts call
    /// <see cref="EnsureResumed"/> first.
    /// </summary>
    private async Task SuspendIfHiddenAsync()
    {
        if (!_coreReady || IsVisible) return;
        try
        {
            if (!WebView.CoreWebView2.IsSuspended)
            {
                await WebView.CoreWebView2.TrySuspendAsync();
            }
        }
        catch
        {
            // Not suspendable right now (e.g. a navigation in flight); try next time.
        }
    }

    private void EnsureResumed()
    {
        _suspendTimer.Stop();
        try
        {
            if (WebView.CoreWebView2.IsSuspended)
            {
                WebView.CoreWebView2.Resume();
            }
        }
        catch
        {
            // Resume is best-effort; a navigation resumes the view regardless.
        }
    }

    // ---- Show/Close hooks ----------------------------------------------------

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Hide instead of close so the WebView stays alive and reopening is instant.
        e.Cancel = true;
        SavePlacement();
        ResetSsoFlow();
        _pendingPostLoadJs = null;
        Hide();
        _suspendTimer.Stop();
        _suspendTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        base.OnClosed(e);
    }

    public new void Show()
    {
        _suspendTimer.Stop();
        if (_coreReady) EnsureResumed();
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
