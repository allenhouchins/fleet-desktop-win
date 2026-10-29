# Fleet Desktop for Windows

Fleet Desktop is a native Windows application that provides end users with a self-service portal for [Fleet](https://fleetdm.com). It integrates with Fleet's [orbit](https://fleetdm.com/docs/get-started/anatomy#orbit) agent (fleetd) to give users direct access to device management features without needing to open a browser.

This is the Windows companion to the [macOS version](https://github.com/allenhouchins/fleet-desktop) and follows the same principles and use cases.

## Features

- **Native Windows app** built with .NET 8 + WPF, for **x64 and ARM64** Windows
- **WebView2** embedded browser (Chromium/Edge-based, modern and secure)
- **Self-service portal** embedded in a native window
- **Automatic token refresh** handles hourly token rotation transparently
- **Loading screen** with Fleet logo while the portal loads
- **File download support** — downloads save to the user's Downloads folder
- **Light/dark mode** respects the user's system appearance
- **`fleet://` URL scheme** for deep linking to Self-service, Policies, Software, triggering refetches, and one-click "Update all" / "Install all" flows
- **Taskbar overlay badge** showing the count of failing policies shown to the end user (policies hidden by the IT admin are ignored; Windows equivalent of the macOS Dock badge); the page auto-reloads on reopen if the count changed while the window was closed
- **End-user notification toasts** — `FleetDesktop.exe notify --url …` shows Fleet's patch notification page as a toast with the same exit-code contract as the macOS app (see [Notifications](#notifications))
- **Window position and size** are remembered between launches
- **fleetd required** — both the app and installer require Fleet's orbit agent to be installed and enrolled
- **Authenticode-signed MSI installer** for secure enterprise distribution

## Requirements

- Windows 10 (version 1809 / build 17763) or newer — x64 or ARM64
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (pre-installed on Windows 11 and recent Windows 10 builds)
- Fleet's orbit agent (fleetd) installed and enrolled
- `C:\Program Files\Orbit\bin\orbit\orbit.exe` (created by fleetd's MSI)
- `C:\Program Files\Orbit\identifier` (created by orbit on enrollment, rotates hourly)

## Installation

### From Releases

1. Download the latest `fleet_desktop-v*-x64.msi` (or `-arm64.msi` for Windows on ARM) from the [Releases](https://github.com/allenhouchins/fleet-desktop-win/releases) page
2. Double-click the `.msi` file to run the installer
3. Follow the installation wizard

The installer requires fleetd to be installed first — it checks for `C:\Program Files\Orbit\bin\orbit\orbit.exe` before proceeding. If that file is missing, the installer aborts with an error. The app is placed in `C:\Program Files\Fleet Desktop\`. On upgrades, the installer gracefully closes Fleet Desktop before installing.

### Via Fleet (Software)

Upload the `.msi` to Fleet as a software installer. Fleet Desktop will appear in the software catalog for deployment.

### Via Intune

The MSI can be wrapped as a Win32 app and deployed via Intune. The installer's MDM-equivalent check (orbit must be installed) ensures fleetd is deployed first.

## How It Works

1. **Reads the Fleet URL** from the `Fleet osquery` Windows service's registration — the same place orbit itself starts from (see [Configuration sources](#configuration-sources))
2. **Reads the device token** from `<root-dir>\identifier` — `<root-dir>` comes from the same service registration, defaulting to `C:\Program Files\Orbit\`
3. **Opens the self-service portal** at `{FleetURL}/device/{token}/self-service` in an embedded WebView2 window

### Token Rotation

The device token in `C:\Program Files\Orbit\identifier` rotates every hour. Fleet Desktop handles this automatically:

- A background timer checks the identifier file every 60 seconds
- On HTTP 401/403 errors or error-page detection, the app immediately checks for a new token and retries (up to 3 attempts with 5-second delays)
- Token refreshes are invisible to the user — the page silently reloads with the new token

### File Downloads

When Fleet serves downloadable content, files are saved to the user's `Downloads` folder with deduplicated names (e.g., `installer (1).msi` if the original exists).

The macOS app auto-opens `.mobileconfig` MDM profiles; Windows doesn't have an equivalent file format, so all downloads are saved without auto-launch. Users can install MSIs / EXEs from their Downloads folder.

### Notifications

Fleet's end-user patch notifications are shown by the same binary, as a borderless toast anchored bottom-right of the screen the cursor is on:

```powershell
& "C:\Program Files\Fleet Desktop\FleetDesktop.exe" notify --url "https://fleet.example.com/device/<token>/notifications/<uuid>"
```

The process returns as soon as the toast is on screen; a detached child process owns the window until the page dismisses it, the user presses **Ctrl+Shift+X** (a fallback for when the page's own dismiss doesn't work), or 10 minutes pass. The exit code is the contract Fleet records — the values are identical to the macOS app's:

| Exit code | Meaning |
|-----------|---------|
| `0` | Displayed |
| `2` | Usage error (bad or missing `--url`; it must be `https://` with a host) |
| `30` | The page did not load (network failure, or nothing rendered within 30s) |
| `31` | The page returned an HTTP error, or rendered Fleet's error copy (expired device token) |
| `40` | No user is logged in at the console |
| `41` | The screen is locked |
| `42` | No display is attached |
| `50` | Another notification is already displayed |
| `70` | Internal error |

fleetd runs scripts as `SYSTEM` in session 0, where nothing drawn is visible, so when `notify` finds itself there it launches the toast in the console user's session itself (the Windows counterpart of the macOS script's `launchctl asuser`). A caller can check for support before invoking it: the MSI writes `HKLM\Software\FleetDM\FleetDesktop\Capabilities\notify = 1` (the counterpart of `FleetDesktopCapabilities` in the macOS `Info.plist`) and `HKLM\Software\FleetDM\FleetDesktop\InstallDir`.

The page talks to the toast through the same bridge as on macOS — `window.webkit.messageHandlers.fleetDesktop.postMessage({ v: 1, action, payload })` — which the toast provides on WebView2 as a shim, so Fleet's notification page needs no platform-specific code. Messages are accepted only from the main frame of the origin (scheme, host and port) the toast was opened with. Actions: `ready`, `primary`, `dismiss`, `error`, `resize`, `log`.

Collecting the exit code from PowerShell needs care, because the app is a GUI-subsystem executable: the call operator (`& $exe notify ...`) doesn't wait for it at all unless its output is piped, and `Start-Process -Wait` waits for every descendant too — including the detached child that owns the toast — so it only returns once the toast closes. Start it through `System.Diagnostics.Process` and call `WaitForExit()` (see `dev-notify.ps1`), or pipe its output (`& $exe notify --url $url | Out-Null; exit $LASTEXITCODE`), which returns as soon as the parent exits because the child's stdio is not inherited. `dev-notify.ps1` shows a toast for a URL and prints the exit code; `dev-notify-smoke.ps1` asserts the exit-code contract for every path that exits before a window appears, and runs in CI.

The toast logs to `%LOCALAPPDATA%\FleetDesktop\Logs\notify.log` (the SYSTEM profile's, for the parent half when run by fleetd). The notification URL is never logged, since it carries the device token.

### Security

- The Fleet server URL must use HTTPS — the app refuses to start with a cleartext `http://` URL rather than send the device token over the wire unencrypted
- Device tokens are validated (alphanumerics plus `-`/`_` only) so path separators and URL metacharacters can never reach a constructed URL, and are URL-encoded on top of that
- SSO flows are pinned to the current identity-provider host: hops to a new host are only allowed via server redirects or scripted navigations (multi-host IdP chains still work), user link clicks to unrelated hosts open in the default browser, and flows expire after 10 minutes
- During an SSO flow, navigations to a known authenticator app scheme (`com-okta-authenticator`, used by Okta Verify for FastPass) are handed to that app; any other custom scheme ends the flow
- The transparency link's redirect is resolved by the app and only an absolute destination is opened in the default browser, so the device token never reaches the browser
- Navigations to the Fleet host are only allowed over HTTPS; a cleartext hop would carry the device token
- External links restricted to `https`, `http`, and `mailto` schemes
- The `notify` toast is confined to the origin it was opened with, accepts bridge messages only from that origin's main frame, and opens `window.open` / link clicks in the default browser
- Downloads keep only the final path component of the server-supplied filename and always land in the user's Downloads folder
- The `FLEET_URL` environment-variable override is compiled out of Release builds — a user-writable env var can't redirect the device token to another host
- The app's native libraries are installed next to the EXE under `Program Files` rather than extracted to the user's `%TEMP%` at launch, so nothing is loaded from a user-writable directory
- WebView2 uses a per-launch user data folder with password autosave and autofill disabled (no cookies or cache persist between sessions); stale folders from earlier launches are swept on startup. The main window closes WebView2's web-message channel (it never uses it), and the toast enables it only for its origin-checked bridge
- The single-instance named pipe is scoped to the current user's SID and restricted with `PipeOptions.CurrentUserOnly`, so other local users can't connect to or squat it, and it accepts at most 8 KB per message
- The `notify` child process is started with raw `CreateProcess` so that exactly one handle (the handshake pipe) is inherited — fleetd reads script output until EOF, and a child holding the caller's stdout open would make the script look hung
- Mutable state protected by a lock for thread safety
- The fleet:// URL scheme handler is registered machine-wide via the MSI

## Development

### Project structure

```
fleet-desktop-win/
├── FleetDesktop/
│   ├── FleetDesktop.csproj      # .NET 8 WPF project
│   ├── App.xaml / App.xaml.cs   # Application entry: CLI routing, single-instance
│   ├── Cli.cs                   # Argument parsing, exit codes
│   ├── MainWindow.xaml(.cs)     # WebView2 host, loading overlay, downloads, SSO policy
│   ├── FleetService.cs          # Config, token mgmt, badge polling, fleet:// handling
│   ├── NotifyCommand.cs         # `notify`: parent/child handshake, session hop, toast lock
│   ├── ToastWindow.xaml(.cs)    # Borderless notification toast + JS bridge
│   ├── FleetErrorPage.cs        # Shared detection of Fleet's error page
│   ├── SingleInstance.cs        # Mutex + named pipe URL dispatch
│   ├── TaskbarBadge.cs          # Overlay icon rendering
│   ├── WindowsTheme.cs          # Dark-mode title bar / overlay
│   ├── WindowPlacement.cs       # Remembered window bounds (HKCU)
│   ├── WebViewProfiles.cs       # Per-launch WebView2 profile folders
│   ├── NativeMethods.cs         # Win32 interop
│   ├── app.manifest             # Per-monitor DPI, supportedOS
│   └── Assets/
│       ├── app.ico
│       └── fleet-logo.png
├── Installer/
│   ├── FleetDesktop.Installer.wixproj   # WiX v5 SDK project
│   ├── Product.wxs              # MSI definition
│   └── License.rtf
├── build.ps1                    # Local build script (-Runtime win-x64|win-arm64)
├── sign.ps1                     # Authenticode signing (PFX or Azure Trusted Signing)
├── dev-notify.ps1               # Show a toast for a URL, print the exit code
├── dev-notify-smoke.ps1         # Assert the notify exit-code contract (runs in CI)
└── .github/workflows/
    ├── build.yml                # CI compile check: unsigned MSI artifact, no secrets
    └── build-and-release.yml    # Tag push / manual: signs and creates a GitHub Release
```

### Building locally

You need:

- Windows 10/11 (64-bit)
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- The build automatically restores the WiX toolset via NuGet — no separate install needed

```powershell
# Build the x64 MSI (Release configuration)
.\build.ps1

# Build the ARM64 MSI
.\build.ps1 -Runtime win-arm64

# EXE only — skip the MSI step
.\build.ps1 -SkipMsi

# Debug build
.\build.ps1 -Configuration Debug
```

The output MSI lands at `Installer\bin\<x64|arm64>\Release\fleet_desktop-v<version>-<x64|arm64>.msi`. The publish output is a single-file EXE plus the native DLLs it loads (WPF's and `WebView2Loader.dll`); the installer harvests the whole publish directory.

To run the app without building an MSI:

```powershell
dotnet run --project FleetDesktop\FleetDesktop.csproj
```

The first run will fail unless fleetd is installed locally — that's expected. To develop without fleetd, set both env vars to point at a fake URL and a directory holding a fake `identifier` file:

```powershell
$env:FLEET_URL = "https://fleet.example.com"
$env:ORBIT_ROOT_DIR = "C:\dev\fake-orbit"
# create fake-orbit\identifier with any non-empty string
dotnet run --project FleetDesktop\FleetDesktop.csproj
```

### Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `FLEET_URL` | _(read from service registry)_ | Override the Fleet server URL. **Debug builds only** — the override is compiled out of Release builds so a user-writable env var can't redirect the device token |
| `ORBIT_ROOT_DIR` | _(read from service registry, falls back to `C:\Program Files\Orbit`)_ | Override the directory `identifier` is read from |

### Configuration sources

The app reads orbit's own configuration from the `Fleet osquery` service registration under `HKLM\SYSTEM\CurrentControlSet\Services\Fleet osquery`, so it needs no configuration of its own. Both forms fleetd has used are supported:

| Source | Purpose |
|--------|---------|
| `Environment` value: `ORBIT_FLEET_URL=…` | Fleet server URL — fleetd MSIs built since September 2026 configure orbit through the service's environment block. Checked first. |
| `Environment` value: `ORBIT_ROOT_DIR=…` | Orbit root directory — used to locate `identifier` |
| `ImagePath` value: `--fleet-url "…"` | Fleet server URL — earlier fleetd MSIs passed it as a command-line argument. Hosts enrolled with those keep this form across orbit's self-updates. |
| `ImagePath` value: `--root-dir "…"` | Orbit root directory, same vintage |
| `<root-dir>\identifier` | Device authentication token (rotates hourly) |

A scheme-less value such as `fleet.example.com:8080` is treated as HTTPS, matching orbit. If no URL is found, or it isn't a valid HTTPS URL, the app displays an error naming the source it looked in and quits.

> **Note:** Fleet Desktop requires fleetd to be installed and enrolled. The MSI refuses to install if `C:\Program Files\Orbit\bin\orbit\orbit.exe` is missing.

### URL scheme

The MSI registers the `fleet://` URL scheme machine-wide. Other tools and scripts can open specific pages:

| URL | Action |
|-----|--------|
| `fleet://self-service` | Opens the Self-service tab |
| `fleet://software` | Opens the Software tab |
| `fleet://policies` | Opens the Policies tab |
| `fleet://refetch` | Triggers a device refetch and opens the app |
| `fleet://update_all` | Opens Self-service and clicks its "Update all" button |
| `fleet://install_all` | Opens Self-service and clicks its "Install all" button — Fleet's confirmation modal still requires the user to confirm |
| `fleet://install_all?category_id=3` | Same, but filters Self-service to that category first so the install is scoped to it |
| `fleet://anything-else` | Brings the app to the foreground |

The dash forms `fleet://update-all` and `fleet://install-all` are also accepted.

Example usage from a script:

```powershell
Start-Process "fleet://self-service"
Start-Process "fleet://refetch"
```

### Command-line flags

| Flag | Purpose |
|------|---------|
| `--hidden` (or `--minimized` / `/hidden`) | Start without showing the window, but preload the page so the first open is instant. Used for autostart-on-login scenarios. The window appears when the user clicks the taskbar icon or a `fleet://` URL is opened. |
| `fleet://<host>` | Open directly to the given page. (Set automatically by Windows when handling a URL scheme.) |
| `notify --url <https url>` | Show an end-user notification toast; see [Notifications](#notifications). |
| `help` / `--help` | Print usage. |

## CI/CD

There are two GitHub Actions workflows:

### Build (`.github/workflows/build.yml`)

Runs on every push to `main` and on pull requests (doc-only changes are skipped). This is a compile-and-package check only — it uses **no secrets**, so it also passes on pull requests from forks:

1. Restores .NET dependencies (the WiX toolset comes with the wixproj's PackageReferences)
2. Publishes a self-contained, single-file EXE — once per architecture (x64 and ARM64), in a matrix
3. Builds the MSI for that architecture
4. Runs `dev-notify-smoke.ps1` against the x64 build to assert the `notify` exit-code contract
5. Uploads the **unsigned** MSIs as workflow artifacts (retained for 30 days)

### Build and Release (`.github/workflows/build-and-release.yml`)

Triggered by pushing a `v*` tag or manually via `workflow_dispatch`. Signing credentials are injected only here: the EXEs and MSIs for both architectures are Authenticode-signed (if secrets are configured), then a GitHub Release is created with both MSIs attached.

All third-party GitHub Actions in both workflows are pinned to commit SHAs (tags kept as comments) so a moved tag upstream can't silently change what runs in CI.

### Releasing a new version

1. Update `<Version>` in `FleetDesktop/FleetDesktop.csproj` (and `assemblyIdentity` in `app.manifest`), and add an entry to `CHANGELOG.md`
2. Push to `main` (the Build workflow runs automatically to verify)
3. Go to **Actions → Build and Release → Run workflow** to create a GitHub Release

## Code signing

Authenticode-signing the MSI is **strongly recommended** for production deployments. An unsigned MSI will trigger SmartScreen warnings and many enterprises block them outright.

The build supports two signing backends — pick whichever fits your situation:

### Option A: Azure Trusted Signing (recommended for ease)

Microsoft's managed code-signing service. **~$9.99 USD/month**, no hardware token, integrates directly with `signtool.exe`. Best path if you're starting from zero.

1. Set up an Azure Trusted Signing account: https://learn.microsoft.com/azure/trusted-signing/quickstart
2. Create a "Standard" certificate profile (or "Public Trust" once you've verified your identity)
3. Create an Entra ID app registration with the **Trusted Signing Certificate Profile Signer** role on the signing account
4. Add the following secrets to your GitHub repo (Settings → Secrets and variables → Actions):

   | Secret | Description |
   |--------|-------------|
   | `AZURE_TS_ENDPOINT` | e.g. `https://eus.codesigning.azure.net/` (region-specific) |
   | `AZURE_TS_ACCOUNT` | Your Trusted Signing account name |
   | `AZURE_TS_PROFILE` | Your certificate profile name |
   | `AZURE_TENANT_ID` | Entra ID tenant ID |
   | `AZURE_CLIENT_ID` | App registration client ID |
   | `AZURE_CLIENT_SECRET` | App registration client secret |

The build script detects these and uses Trusted Signing automatically.

### Option B: Traditional PFX certificate

Use a standard OV/EV code signing certificate from DigiCert, SSL.com, Sectigo, etc.

1. Export your certificate as a `.pfx` file with a password
2. Base64-encode it: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx")) | Set-Clipboard`
3. Add to GitHub Secrets:

   | Secret | Description |
   |--------|-------------|
   | `WINDOWS_PFX_BASE64` | Base64-encoded `.pfx` |
   | `WINDOWS_PFX_PASSWORD` | Password for the `.pfx` |

**Note:** as of June 2023, the CA/Browser Forum requires standard code signing certs to be issued on hardware tokens (HSMs / FIPS 140-2 Level 2). This makes traditional CI signing more complex — you'd either need a cloud HSM solution (DigiCert KeyLocker, SSL.com eSigner, etc.) or pre-sign locally and upload the signed binaries. **This is why Azure Trusted Signing is the easier path** for new projects.

### Option C: No signing (development / internal use only)

If neither set of signing secrets is configured, the build still completes — the MSI will just be unsigned. Useful for iterating on the build, but expect SmartScreen warnings when running the installer.

## Contributing

Contributions are welcome.

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Open a Pull Request

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.

## Support

- [Open an issue](https://github.com/allenhouchins/fleet-desktop-win/issues) on GitHub
- [Fleet documentation](https://fleetdm.com/docs)
