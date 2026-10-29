using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using static FleetDesktop.NativeMethods;

namespace FleetDesktop;

/// <summary>How a toast ended. Port of the macOS ToastOutcome.</summary>
internal abstract record ToastOutcome
{
    /// <summary>The user activated the page's primary action.</summary>
    public sealed record PrimaryAction(string? Id) : ToastOutcome;

    /// <summary>The user dismissed the toast.</summary>
    public sealed record Dismissed(string? Reason) : ToastOutcome;

    /// <summary>The display timeout expired with no interaction.</summary>
    public sealed record TimedOut : ToastOutcome;

    /// <summary>The page never reached first paint. Nothing was shown.</summary>
    public sealed record LoadFailed(string Detail) : ToastOutcome;

    /// <summary>The page returned an HTTP error status on the main frame. Nothing was shown.</summary>
    public sealed record HttpError(int Status) : ToastOutcome;

    /// <summary>The page loaded with HTTP 200 but rendered Fleet's error copy, which is
    /// what an expired device token looks like. Nothing was shown.</summary>
    public sealed record ContentError(string Detail) : ToastOutcome;

    /// <summary>No display is attached, so there is nothing to draw on. Nothing was shown.</summary>
    public sealed record NoDisplay : ToastOutcome;
}

/// <summary>
/// A borderless, topmost, non-activating toast anchored bottom-right, hosting a
/// WebView2 on a rounded card. <see cref="Displayed"/> fires when it reaches the
/// screen, <see cref="Finished"/> when it is gone — or immediately, with a failure, if
/// it never appeared. Each fires once. Port of ToastWindow.swift.
///
/// The window is created on screen at alpha 0 (and click-through) so WebView2 can
/// initialize and paint before anything is visible; it then fades in. Nothing is shown
/// before first paint, and a toast the user never saw is never reported as displayed.
/// </summary>
public partial class ToastWindow : Window
{
    // ---- Layout ----

    /// <summary>Matches the Figma update card, in device-independent pixels.</summary>
    private const double CardWidth = 525;
    private const double CardHeight = 318;

    /// <summary>Gap from the screen's working area.</summary>
    private const double ScreenMargin = 16;

    /// <summary>Smallest card worth showing. The maximum is the working area less
    /// margins, computed per screen.</summary>
    private const double MinCardHeight = 200;

    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(350);

    // ---- Timeouts ----

    /// <summary>Only covers a page that connects then never renders; real network
    /// failures are reported by the navigation events long before this.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Safety net, not the expected way to close: that's the page's dismiss
    /// action. Without it a page that never sends <c>dismiss</c> leaves an
    /// undismissable window.</summary>
    public static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(600);

    /// <summary>Last resort, in case a wedged WebView2 means neither timeout above fires.</summary>
    public static readonly TimeSpan WatchdogLimit = LoadTimeout + DisplayTimeout + TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ReadyGrace = TimeSpan.FromMilliseconds(1500);

    private const int DefaultHttpsPort = 443;
    private const int BreakglassHotKeyId = 1;

    // ---- Bridge ----

    /// <summary>
    /// The page posts to <c>window.webkit.messageHandlers.fleetDesktop</c> — the
    /// WKWebView bridge the macOS app exposes. This shim provides the same object on
    /// WebView2, forwarding to <c>window.chrome.webview.postMessage</c>, so Fleet's
    /// notification page needs no platform-specific code.
    /// </summary>
    private const string BridgeShimScript = """
        (function () {
          var post = function (message) {
            try { window.chrome.webview.postMessage(message); } catch (e) {}
          };
          window.webkit = window.webkit || {};
          window.webkit.messageHandlers = window.webkit.messageHandlers || {};
          window.webkit.messageHandlers.fleetDesktop = { postMessage: post };
        })();
        """;

    /// <summary>
    /// Reports the document's content height whenever it changes, so a page listing
    /// an arbitrary number of items isn't clipped. Injected rather than left to the
    /// page, so it works without the page implementing anything. The page can still
    /// post <c>resize</c> itself.
    /// </summary>
    private const string AutoSizeScript = """
        (function () {
          var last = 0;
          function report() {
            if (!document.body) { return; }
            var height = Math.ceil(document.body.scrollHeight);
            if (!height || Math.abs(height - last) < 2) { return; }
            last = height;
            window.chrome.webview.postMessage({ v: 1, action: "resize", payload: { height: height } });
          }
          function init() {
            if (window.ResizeObserver && document.body) {
              new ResizeObserver(report).observe(document.body);
            }
            window.addEventListener("load", report);
            report();
          }
          if (document.readyState === "loading") {
            document.addEventListener("DOMContentLoaded", init);
          } else {
            init();
          }
        })();
        """;

    /// <summary>Classifies the loaded document as empty, an error page, or usable.</summary>
    private static string ContentProbe => $$"""
        (function () {
          if (!document.body || document.body.children.length === 0) { return "empty"; }
          var text = document.body.innerText || "";
          return {{FleetErrorPage.MatchesExpression}} ? "error" : "ok";
        })();
        """;

    // ---- State ----

    private readonly Uri _url;

    /// <summary>Host and port the page must post from, captured from the URL we load.
    /// Without the port, a Fleet server on :8080 would also trust :9999 on that host.</summary>
    private readonly string _expectedHost;
    private readonly int _expectedPort;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private bool _coreReady;
    private bool _didDisplay;
    private bool _didFinish;

    /// <summary>The page signalled <c>ready</c>, or navigation finished and the grace period lapsed.</summary>
    private bool _hasPainted;

    private bool _hotKeyRegistered;
    private double _cardHeightDip = CardHeight;
    private int? _pendingStatusCode;
    private string? _pendingNavigationUri;

    private DispatcherTimer? _loadDeadline;
    private DispatcherTimer? _readyGrace;
    private DispatcherTimer? _displayDeadline;
    private DispatcherTimer? _fade;

    /// <summary>Fires once, when the toast is on screen.</summary>
    internal event Action? Displayed;

    /// <summary>Fires once, when the toast is gone or has failed before appearing.</summary>
    internal event Action<ToastOutcome>? Finished;

    public ToastWindow(Uri url)
    {
        InitializeComponent();
        _url = url;
        _expectedHost = url.Host.ToLowerInvariant();
        _expectedPort = url.IsDefaultPort ? DefaultHttpsPort : url.Port;
        ApplyTheme(WindowsTheme.IsDarkMode());
    }

    // ---- Presenting -------------------------------------------------------------

    /// <summary>Puts the (invisible) window on screen, loads the page, and arms the
    /// load deadline.</summary>
    public void Present()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        // Non-activating so it never steals focus; tool window so it stays out of
        // Alt+Tab; layered so it can fade; click-through until it is visible, so an
        // invisible window never swallows clicks meant for whatever is underneath.
        var exStyle = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA);
        WindowsTheme.ApplyRoundedCorners(_hwnd);

        Anchor();
        Show();

        _ = InitializeWebViewAsync();

        _loadDeadline = After(LoadTimeout, () =>
        {
            if (_hasPainted) return;
            Finish(new ToastOutcome.LoadFailed($"Page did not render within {(int)LoadTimeout.TotalSeconds}s."));
        });
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = WebViewProfiles.CreateUserDataFolder("WebView2Notify");
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userDataFolder).ConfigureAwait(true);
            await WebView.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            var core = WebView.CoreWebView2;
            var settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            // Failures are classified and reported by the app; an error page would be
            // mistaken for content.
            settings.IsBuiltInErrorPageEnabled = false;
            settings.IsWebMessageEnabled = true;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeShimScript).ConfigureAwait(true);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(AutoSizeScript).ConfigureAwait(true);

            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.WebResourceResponseReceived += OnWebResourceResponseReceived;

            _coreReady = true;
            core.Navigate(_url.AbsoluteUri);
        }
        catch (Exception ex)
        {
            NotifyLog.Warn($"WebView2 initialization failed: {ex.Message}");
            Finish(new ToastOutcome.LoadFailed("The WebView2 runtime is unavailable."));
        }
    }

    /// <summary>Anchors the card bottom-right on the screen under the cursor — where the
    /// user is actually looking — sized in that screen's physical pixels.</summary>
    private void Anchor()
    {
        var (work, scale) = ActiveMonitor();
        var width = (int)Math.Round(CardWidth * scale);
        var height = (int)Math.Round(_cardHeightDip * scale);
        var margin = (int)Math.Round(ScreenMargin * scale);
        var x = work.Right - width - margin;
        var y = work.Bottom - height - margin;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE);
    }

    /// <summary>Working area (physical pixels) and DPI scale of the screen under the cursor.</summary>
    private static (RECT Work, double Scale) ActiveMonitor()
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            info.rcWork = new RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
        }
        var scale = 1.0;
        try
        {
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            {
                scale = dpiX / 96.0;
            }
        }
        catch
        {
            // Pre-8.1 builds: assume 100%.
        }
        return (info.rcWork, scale);
    }

    /// <summary>Makes the toast visible: click-through off, fade in, hotkey on.</summary>
    private void ShowToast()
    {
        if (_didDisplay || _didFinish) return;
        _didDisplay = true;
        CancelTimers();

        var exStyle = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle & ~WS_EX_TRANSPARENT));
        Anchor();
        FadeTo(255, easeOut: true, onDone: null);

        // Cmd+Shift+X on macOS; Ctrl+Shift+X here. Registered only while the toast is
        // up, since it takes the shortcut from every other app for that time. Fails if
        // another app already holds it; the toast still works, it just loses the
        // breakglass.
        _hotKeyRegistered = RegisterHotKey(_hwnd, BreakglassHotKeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_X);
        if (!_hotKeyRegistered)
        {
            NotifyLog.Warn($"breakglass hotkey unavailable (error {Marshal.GetLastWin32Error()})");
        }

        NotifyLog.Info("toast displayed");
        Displayed?.Invoke();

        _displayDeadline = After(DisplayTimeout, () => FadeOutAndFinish(new ToastOutcome.TimedOut()));
    }

    // ---- Finishing --------------------------------------------------------------

    /// <summary>For outcomes the user caused, so the fade is visible before the process exits.</summary>
    private void FadeOutAndFinish(ToastOutcome outcome)
    {
        if (_didFinish) return;
        if (!_didDisplay)
        {
            Finish(outcome);
            return;
        }
        CancelTimers();
        FadeTo(0, easeOut: false, onDone: () =>
        {
            try { Hide(); } catch { }
            Finish(outcome);
        });
    }

    /// <summary>Single exit point, guarded so nothing reports twice.</summary>
    private void Finish(ToastOutcome outcome)
    {
        if (_didFinish) return;
        _didFinish = true;
        CancelTimers();
        Teardown();
        Finished?.Invoke(outcome);
    }

    private void CancelTimers()
    {
        _loadDeadline?.Stop();
        _readyGrace?.Stop();
        _displayDeadline?.Stop();
        _loadDeadline = null;
        _readyGrace = null;
        _displayDeadline = null;
    }

    private void Teardown()
    {
        if (_hotKeyRegistered)
        {
            UnregisterHotKey(_hwnd, BreakglassHotKeyId);
            _hotKeyRegistered = false;
        }
        _fade?.Stop();
        _fade = null;
        try
        {
            if (_coreReady) WebView.CoreWebView2.Stop();
        }
        catch
        {
            // Already gone.
        }
    }

    // ---- Animation --------------------------------------------------------------

    /// <summary>Fades the whole window (WebView2 included — a layered top-level window
    /// composes its children) by stepping the layered alpha over <see cref="AnimationDuration"/>.</summary>
    private void FadeTo(byte target, bool easeOut, Action? onDone)
    {
        _fade?.Stop();
        var started = DateTime.UtcNow;
        var from = target == 0 ? 255.0 : 0.0;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, (DateTime.UtcNow - started).TotalMilliseconds / AnimationDuration.TotalMilliseconds);
            var eased = easeOut ? 1 - Math.Pow(1 - t, 2) : t * t;
            var alpha = (byte)Math.Round(from + (target - from) * eased);
            SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);
            if (t >= 1.0)
            {
                timer.Stop();
                if (ReferenceEquals(_fade, timer)) _fade = null;
                onDone?.Invoke();
            }
        };
        _fade = timer;
        timer.Start();
    }

    private DispatcherTimer After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return timer;
    }

    // ---- Theme -----------------------------------------------------------------

    /// <summary>White card in light mode, dark grey in dark mode, with a hairline rim.
    /// The dark rim is stronger on purpose: against a dark desktop the shadow is
    /// invisible, so the rim is the only thing separating the card.</summary>
    private void ApplyTheme(bool dark)
    {
        var fill = dark ? Color.FromRgb(0x29, 0x29, 0x29) : Colors.White;
        var rim = dark ? Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x29, 0x00, 0x00, 0x00);
        Background = new SolidColorBrush(fill);
        Card.Background = new SolidColorBrush(fill);
        Card.BorderBrush = new SolidColorBrush(rim);
        // Shows through until the page paints, and behind a page with a transparent body.
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(fill.R, fill.G, fill.B);
    }

    // ---- Resizing ---------------------------------------------------------------

    /// <summary>Applies a requested content height (CSS pixels, which match DIPs),
    /// keeping the toast anchored bottom-right.</summary>
    private void Resize(double requested)
    {
        var (work, scale) = ActiveMonitor();
        var available = work.Height / scale - 2 * ScreenMargin;
        var maxHeight = Math.Max(MinCardHeight, available);
        var height = Math.Min(Math.Max(requested, MinCardHeight), maxHeight);
        // The observer fires again after we resize the view, so ignoring an unchanged
        // height is what stops that becoming a feedback loop.
        if (Math.Abs(height - _cardHeightDip) <= 0.5) return;
        NotifyLog.Debug($"resizing card to {(int)height}dip (requested {(int)requested})");
        _cardHeightDip = height;
        Anchor();
    }

    // ---- JS bridge --------------------------------------------------------------

    /// <summary>Receives the page's <c>postMessage</c> calls. Unknown actions and a
    /// missing <c>payload</c> are tolerated, so a newer page keeps working against an
    /// older binary. Only the top-level document reaches this handler; iframes post to
    /// their own, separate channel, so an embedded doc or OAuth widget can't claim the
    /// primary action or dismiss the toast.</summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrusted(e.Source))
        {
            NotifyLog.Warn("bridge: dropped message from an untrusted origin");
            return;
        }

        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch
        {
            NotifyLog.Debug("bridge: dropped malformed message");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("action", out var actionElement) ||
                actionElement.ValueKind != JsonValueKind.String)
            {
                NotifyLog.Debug("bridge: dropped malformed message");
                return;
            }
            var action = actionElement.GetString() ?? "";
            JsonElement? payload = root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p : null;

            switch (action)
            {
                case "ready":
                    MarkPainted();
                    break;
                case "primary":
                    FadeOutAndFinish(new ToastOutcome.PrimaryAction(StringField(payload, "id")));
                    break;
                case "dismiss":
                    FadeOutAndFinish(new ToastOutcome.Dismissed(StringField(payload, "reason")));
                    break;
                case "error":
                    Finish(new ToastOutcome.ContentError(StringField(payload, "message") ?? "The page reported an error."));
                    break;
                case "resize":
                    if (payload is { } rp && rp.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number &&
                        h.TryGetDouble(out var height) && double.IsFinite(height))
                    {
                        Resize(height);
                    }
                    break;
                case "log":
                    if (StringField(payload, "message") is { } text) NotifyLog.Debug($"page: {text}");
                    break;
                default:
                    NotifyLog.Debug($"bridge: ignoring unknown action {action}");
                    break;
            }
        }
        catch (JsonException)
        {
            NotifyLog.Debug("bridge: dropped malformed message");
        }
    }

    private static string? StringField(JsonElement? payload, string name) =>
        payload is { } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Whether a document URI belongs to the origin the toast was opened with.</summary>
    private bool IsTrusted(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && IsSameOrigin(uri);

    private bool IsSameOrigin(Uri candidate) =>
        string.Equals(candidate.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(candidate.Host, _expectedHost, StringComparison.OrdinalIgnoreCase) &&
        (candidate.IsDefaultPort ? DefaultHttpsPort : candidate.Port) == _expectedPort;

    // ---- Navigation -------------------------------------------------------------

    /// <summary>Confines the toast to the origin it was opened with. A chromeless window
    /// with no address bar must never end up somewhere the user can't identify, so link
    /// clicks go to the browser and anything else is refused.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }
        if (IsSameOrigin(uri) || string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            _pendingNavigationUri = e.Uri;
            return;
        }

        e.Cancel = true;
        var scheme = uri.Scheme.ToLowerInvariant();
        if (e.IsUserInitiated && scheme is "https" or "http" or "mailto")
        {
            OpenExternal(uri);
            return;
        }
        NotifyLog.Debug($"blocked navigation to {scheme} URL");
    }

    /// <summary>No <c>window.open</c> from a notification.</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme.ToLowerInvariant() is "https" or "http")
        {
            OpenExternal(uri);
        }
    }

    private static void OpenExternal(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>Captures the main document's status for runtimes too old to report it
    /// on <see cref="CoreWebView2NavigationCompletedEventArgs.HttpStatusCode"/>.</summary>
    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            if (e.Request?.Method != "GET") return;
            if (!string.Equals(e.Request.Uri, _pendingNavigationUri, StringComparison.Ordinal)) return;
            _pendingStatusCode = e.Response?.StatusCode;
        }
        catch
        {
            // Ignore malformed events.
        }
    }

    /// <summary>
    /// Navigation finished. A React page mounts after this fires, which is why
    /// <c>ready</c> is preferred. Wait a grace period for it, then show anyway so a page
    /// that doesn't use the bridge still appears.
    /// </summary>
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_hasPainted || _didFinish) return;

        var status = _pendingStatusCode;
        _pendingStatusCode = null;

        if (!e.IsSuccess)
        {
            // A cancelled load is what our own policy decisions produce, so it isn't a
            // failure in itself.
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            NotifyLog.Debug($"navigation failed: {e.WebErrorStatus}");
            Finish(new ToastOutcome.LoadFailed(e.WebErrorStatus.ToString()));
            return;
        }

        // Rejects HTTP errors rather than displaying an error page. Only the main
        // frame is reported here, so a 401 on a subresource can't fail the toast.
        var httpStatus = TryGetHttpStatus(e) ?? status;
        if (httpStatus is >= 400)
        {
            Finish(new ToastOutcome.HttpError(httpStatus.Value));
            return;
        }

        // Finishing is not proof there is anything worth showing: some refusals land
        // on an empty document, an empty 200 looks the same, and Fleet serves 200 with
        // error copy when the device token has expired. All three would otherwise be
        // reported as displayed.
        NotifyLog.Debug("navigation finished; checking the document has content");
        string probe;
        try
        {
            probe = await WebView.CoreWebView2.ExecuteScriptAsync(ContentProbe);
        }
        catch
        {
            probe = "";
        }
        if (_hasPainted || _didFinish) return;

        switch (probe)
        {
            case "\"empty\"":
                Finish(new ToastOutcome.LoadFailed("Page loaded but rendered no content."));
                return;
            case "\"error\"":
                Finish(new ToastOutcome.ContentError("Page reported an error; the device token may have expired."));
                return;
        }

        NotifyLog.Debug("document has content; waiting up to 1.5s for a ready message");
        _readyGrace = After(ReadyGrace, MarkPainted);
    }

    private static int? TryGetHttpStatus(CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            var status = e.HttpStatusCode;
            return status > 0 ? status : null;
        }
        catch
        {
            // Runtime predates the property.
            return null;
        }
    }

    /// <summary>The page has rendered: show it, if it isn't up already.</summary>
    private void MarkPainted()
    {
        if (_hasPainted) return;
        _hasPainted = true;
        _readyGrace?.Stop();
        _readyGrace = null;
        ShowToast();
    }

    // ---- Breakglass -------------------------------------------------------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == BreakglassHotKeyId)
        {
            handled = true;
            FadeOutAndFinish(new ToastOutcome.Dismissed("breakglass"));
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Nothing closes the toast but its own outcome handling.
        e.Cancel = !_didFinish;
        base.OnClosing(e);
    }
}
