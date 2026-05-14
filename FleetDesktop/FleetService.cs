using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FleetDesktop;

/// <summary>
/// Core service: resolves config, reads the device token, owns the browser window,
/// polls for token rotation, and updates the taskbar overlay badge.
///
/// Port of FleetService.swift from the macOS app, adapted to WPF idioms.
/// Mutable state is protected by <see cref="_stateLock"/>.
/// </summary>
internal sealed class FleetService : IDisposable
{
    public const string WindowTitle = "Fleet Desktop";

    /// <summary>Pages accessible via fleet:// URLs.</summary>
    private static readonly HashSet<string> ValidPages =
        new(StringComparer.OrdinalIgnoreCase) { "self-service", "policies", "software" };

    private static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TokenRetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxRetryAttempts = 3;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly object _stateLock = new();
    private readonly string _orbitRoot;
    private readonly string _tokenFile;
    private readonly string? _fleetUrlFromService;

    private MainWindow? _browserWindow;
    private DispatcherTimer? _refreshTimer;

    // Mutable state — guard with _stateLock.
    private string? _baseUrl;
    private string? _currentToken;
    private bool _isSettingUp;
    private int _retryCount;
    private string? _pendingPage;
    private bool _pendingRefetch;
    private bool _userRequestedFleetUi;

    // Main-thread-only.
    private bool _deferredPresentationFromHeadlessLaunch;
    private bool _hidden;

    public FleetService()
    {
        // On Windows, the Fleet URL and root-dir live as command-line arguments
        // baked into the "Fleet osquery" service's ImagePath registry value by
        // fleetd's own WiX installer. (Unlike macOS, there's no fleet_url.txt —
        // that file is written only on darwin from an Apple config profile.)
        var serviceArgs = ReadOrbitServiceArgs();
        _fleetUrlFromService = serviceArgs != null ? ExtractArg(serviceArgs, "fleet-url") : null;

        _orbitRoot = ResolveOrbitRoot(serviceArgs);
        _tokenFile = Path.Combine(_orbitRoot, "identifier");
    }

    private static string ResolveOrbitRoot(string? serviceArgs)
    {
        var fromEnv = Environment.GetEnvironmentVariable("ORBIT_ROOT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }
        if (serviceArgs != null)
        {
            var fromService = ExtractArg(serviceArgs, "root-dir");
            if (!string.IsNullOrWhiteSpace(fromService))
            {
                // fleetd's WiX template uses [ORBITROOT]. → trailing "\." which Path.Combine
                // tolerates, but trim it so error messages show a clean path.
                return fromService.TrimEnd('.', '\\', '/').TrimEnd('\\', '/');
            }
        }
        // Last-resort default install path.
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(programFiles))
        {
            programFiles = @"C:\Program Files";
        }
        return Path.Combine(programFiles, "Orbit");
    }

    /// <summary>
    /// Reads HKLM\SYSTEM\CurrentControlSet\Services\Fleet osquery\ImagePath.
    /// Returns the full command line (exe path + args) or null if the service
    /// isn't installed / can't be read.
    /// </summary>
    private static string? ReadOrbitServiceArgs()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Fleet osquery");
            // GetValue with DoNotExpandEnvironmentNames preserves any %VAR% tokens; the
            // ImagePath for fleetd is typically a plain string but be safe.
            return key?.GetValue("ImagePath", null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a "--name" argument value from a command-line string. Supports both
    /// quoted (--name "value") and unquoted (--name value, --name=value) forms.
    /// </summary>
    private static string? ExtractArg(string commandLine, string name)
    {
        var escaped = Regex.Escape(name);
        // --name "value with spaces"
        var quoted = Regex.Match(commandLine, $@"--{escaped}[=\s]+""([^""]+)""");
        if (quoted.Success) return quoted.Groups[1].Value;
        // --name value-without-spaces  or  --name=value
        var bare = Regex.Match(commandLine, $@"--{escaped}[=\s]+(\S+)");
        return bare.Success ? bare.Groups[1].Value : null;
    }

    // ---- Public entry points -------------------------------------------------

    /// <summary>
    /// Initial run. Resolves config, creates the browser window, and either shows
    /// or defers presentation depending on <paramref name="hidden"/>.
    /// </summary>
    public void Run(string? initialUrl, bool hidden)
    {
        _hidden = hidden;

        if (!string.IsNullOrEmpty(initialUrl))
        {
            // Pre-seed pending state so setup() picks up the right page.
            SetPendingFromUrl(initialUrl, alsoMarkUserRequested: true);
        }

        // Resolve config off the UI thread, then bounce back to create the window.
        Task.Run(Setup);
    }

    /// <summary>Called by App when a forwarded fleet:// URL arrives (named pipe).</summary>
    public void HandleFleetUrl(string urlString)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url) ||
            !string.Equals(url.Scheme, "fleet", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var host = url.Host?.ToLowerInvariant() ?? string.Empty;
        var browserReady = _browserWindow?.IsAvailable == true;

        if (!browserReady)
        {
            lock (_stateLock) { _userRequestedFleetUi = true; }
        }

        if (host == "refetch")
        {
            bool hasConfig;
            lock (_stateLock) { hasConfig = _baseUrl != null; }
            if (hasConfig)
            {
                _ = PerformRefetchAsync();
            }
            else
            {
                lock (_stateLock) { _pendingRefetch = true; }
            }
            ShowWindow();
            return;
        }

        var page = ValidPages.Contains(host) ? host : null;

        if (browserReady && _browserWindow != null)
        {
            if (page != null && TryBuildDeviceUrl(page, out var target))
            {
                _browserWindow.Reload(target!);
            }
            _browserWindow.Show();
            return;
        }

        lock (_stateLock) { _pendingPage = page; }
        // Setup is either running or done; if done, ShowWindow will re-present.
        ShowWindow();
    }

    /// <summary>Called when the user activates the app via taskbar/etc. with no specific URL.</summary>
    public void ShowWindow()
    {
        if (_browserWindow != null && _browserWindow.IsAvailable)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _deferredPresentationFromHeadlessLaunch = false;
                _browserWindow.Show();
            });
            return;
        }

        // Setup may still be running — pending state will get consumed when it completes.
        bool shouldKick;
        lock (_stateLock)
        {
            shouldKick = !_isSettingUp;
            if (shouldKick) _isSettingUp = true;
            _userRequestedFleetUi = true;
        }
        if (shouldKick)
        {
            Task.Run(Setup);
        }
    }

    /// <summary>Reload the current page (View → Reload, F5, Ctrl+R).</summary>
    public void ReloadCurrentPage()
    {
        _browserWindow?.ReloadCurrent();
    }

    public void Dispose()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    // ---- Setup ---------------------------------------------------------------

    private void Setup()
    {
        // Double-guard re-entry.
        lock (_stateLock)
        {
            if (_browserWindow != null && _browserWindow.IsAvailable)
            {
                _isSettingUp = false;
                return;
            }
            // _isSettingUp may already be true (set by ShowWindow/Run); make sure it's true.
            _isSettingUp = true;
        }

        if (!ResolveConfig())
        {
            lock (_stateLock) { _isSettingUp = false; }
            return;
        }

        // Hop to the UI thread to create the window.
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            string page;
            bool refetch;
            lock (_stateLock)
            {
                page = _pendingPage ?? "self-service";
                _pendingPage = null;
                refetch = _pendingRefetch;
                _pendingRefetch = false;
            }

            if (refetch) _ = PerformRefetchAsync();

            if (!TryBuildDeviceUrl(page, out var url))
            {
                ShowFatalError("Unable to construct self-service URL. Check Fleet configuration.");
                return;
            }

            var window = new MainWindow();
            _browserWindow = window;
            window.NavigationError += OnNavigationError;
            window.WindowShown += OnWindowShown;
            window.Preload(url!);

            StartRefreshTimer();

            // Defer the show decision one dispatcher turn so any pending fleet:// URL
            // forwarded via the named pipe is already in pending state.
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                bool userWants;
                lock (_stateLock)
                {
                    userWants = _userRequestedFleetUi;
                    _userRequestedFleetUi = false;
                }

                var showNow = !_hidden || userWants;
                if (showNow)
                {
                    window.Show();
                    _deferredPresentationFromHeadlessLaunch = false;
                }
                else
                {
                    _deferredPresentationFromHeadlessLaunch = true;
                }

                lock (_stateLock) { _isSettingUp = false; }
            }, DispatcherPriority.Background);
        });
    }

    /// <summary>Reads the Fleet URL and device token. Returns true if both are valid.</summary>
    private bool ResolveConfig()
    {
        var fleetUrl = ReadFleetUrl();
        if (string.IsNullOrEmpty(fleetUrl))
        {
            ShowFatalError(
                "Fleet Desktop could not find the Fleet server URL.\n\n" +
                "Expected to read it from the \"Fleet osquery\" Windows service " +
                "(HKLM\\SYSTEM\\CurrentControlSet\\Services\\Fleet osquery\\ImagePath, " +
                "--fleet-url argument).\n\n" +
                "Ensure the Fleet orbit agent (fleetd) is installed and enrolled on this machine. " +
                "Contact your administrator if the problem persists.");
            return false;
        }

        var token = ReadToken();
        if (string.IsNullOrEmpty(token))
        {
            ShowFatalError(
                $"Device token not found or could not be read at {_tokenFile}.\n\n" +
                "Ensure the Fleet orbit agent is enrolled and the identifier file exists.");
            return false;
        }

        lock (_stateLock)
        {
            _baseUrl = fleetUrl.TrimEnd('/');
            _currentToken = token;
        }
        return true;
    }

    // ---- Token refresh / badge polling ---------------------------------------

    private void StartRefreshTimer()
    {
        _refreshTimer?.Stop();
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TokenRefreshInterval,
        };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshTokenIfNeeded();
            _ = FetchDesktopDataAsync();
        };
        _refreshTimer.Start();

        // Kick off an immediate badge fetch so the first update doesn't wait 60s.
        _ = FetchDesktopDataAsync();
    }

    private void RefreshTokenIfNeeded()
    {
        var newToken = ReadToken();
        if (string.IsNullOrEmpty(newToken) || _browserWindow == null) return;

        bool changed;
        lock (_stateLock)
        {
            changed = newToken != _currentToken;
            if (changed)
            {
                _currentToken = newToken;
                _retryCount = 0;
            }
        }
        if (!changed) return;

        if (TryBuildDeviceUrl("self-service", out var url))
        {
            _browserWindow.Reload(url!);
        }
    }

    private void OnNavigationError()
    {
        string? oldToken;
        lock (_stateLock) { oldToken = _currentToken; }

        var newToken = ReadToken();
        if (!string.IsNullOrEmpty(newToken) && newToken != oldToken)
        {
            lock (_stateLock)
            {
                _currentToken = newToken;
                _retryCount = 0;
            }
            if (TryBuildDeviceUrl("self-service", out var url))
            {
                _browserWindow?.Reload(url!);
            }
            return;
        }

        bool retry;
        lock (_stateLock)
        {
            if (_retryCount < MaxRetryAttempts)
            {
                _retryCount++;
                retry = true;
            }
            else
            {
                _retryCount = 0;
                retry = false;
            }
        }
        if (!retry) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(TokenRetryDelay).ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(RefreshTokenIfNeeded);
        });
    }

    private void OnWindowShown()
    {
        RefreshTokenIfNeeded();
    }

    private async Task PerformRefetchAsync()
    {
        if (!TryBuildApiUrl("refetch", out var url)) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
        }
        catch
        {
            return;
        }

        // After a refetch succeeds, sample the badge twice so policy changes
        // (e.g. an app install that flips a policy to passing) show up quickly.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await FetchDesktopDataAsync().ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await FetchDesktopDataAsync().ConfigureAwait(false);
        });
    }

    private async Task FetchDesktopDataAsync()
    {
        if (!TryBuildApiUrl("desktop", out var url)) return;
        try
        {
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<DesktopResponse>(json);
            if (data == null) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _browserWindow?.SetBadge(data.FailingPoliciesCount);
            });
        }
        catch
        {
            // Network errors are silent — next tick will retry.
        }
    }

    private sealed class DesktopResponse
    {
        [JsonPropertyName("failing_policies_count")]
        public int FailingPoliciesCount { get; set; }
    }

    // ---- URL construction ----------------------------------------------------

    private bool TryBuildDeviceUrl(string page, out Uri? result)
    {
        result = null;
        string? baseUrl, token;
        lock (_stateLock) { baseUrl = _baseUrl; token = _currentToken; }
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(token)) return false;
        var encodedToken = Uri.EscapeDataString(token);
        var encodedPage = Uri.EscapeDataString(page);
        return Uri.TryCreate($"{baseUrl}/device/{encodedToken}/{encodedPage}", UriKind.Absolute, out result);
    }

    private bool TryBuildApiUrl(string suffix, out Uri? result)
    {
        result = null;
        string? baseUrl, token;
        lock (_stateLock) { baseUrl = _baseUrl; token = _currentToken; }
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(token)) return false;
        var encodedToken = Uri.EscapeDataString(token);
        return Uri.TryCreate($"{baseUrl}/api/latest/fleet/device/{encodedToken}/{suffix}", UriKind.Absolute, out result);
    }

    private void SetPendingFromUrl(string urlString, bool alsoMarkUserRequested)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url) ||
            !string.Equals(url.Scheme, "fleet", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var host = url.Host?.ToLowerInvariant() ?? string.Empty;
        lock (_stateLock)
        {
            if (host == "refetch")
            {
                _pendingRefetch = true;
            }
            else if (ValidPages.Contains(host))
            {
                _pendingPage = host;
            }
            if (alsoMarkUserRequested) _userRequestedFleetUi = true;
        }
    }

    // ---- Config / file reading ----------------------------------------------

    /// <summary>
    /// Returns the Fleet server URL. Prefers the FLEET_URL env var (useful for
    /// local testing) and falls back to the value parsed from the "Fleet osquery"
    /// service's ImagePath in the registry.
    /// </summary>
    private string? ReadFleetUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable("FLEET_URL");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        return string.IsNullOrWhiteSpace(_fleetUrlFromService) ? null : _fleetUrlFromService;
    }

    private string? ReadToken() => ReadFileTrimmed(_tokenFile);

    private static string? ReadFileTrimmed(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var contents = File.ReadAllText(path).Trim('\r', '\n', ' ', '\t');
            return string.IsNullOrEmpty(contents) ? null : contents;
        }
        catch
        {
            return null;
        }
    }

    // ---- Error display -------------------------------------------------------

    private static void ShowFatalError(string message)
    {
        void Work()
        {
            MessageBox.Show(message, WindowTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown(1);
        }
        if (Application.Current.Dispatcher.CheckAccess())
        {
            Work();
        }
        else
        {
            Application.Current.Dispatcher.Invoke(Work);
        }
    }
}
