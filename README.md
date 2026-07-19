# Fleet Desktop for Windows

Fleet Desktop is a native Windows application that provides end users with a self-service portal for [Fleet](https://fleetdm.com). It integrates with Fleet's [orbit](https://fleetdm.com/docs/get-started/anatomy#orbit) agent (fleetd) to give users direct access to device management features without needing to open a browser.

This is the Windows companion to the [macOS version](https://github.com/allenhouchins/fleet-desktop) and follows the same principles and use cases.

## Features

- **Native Windows app** built with .NET 8 + WPF
- **WebView2** embedded browser (Chromium/Edge-based, modern and secure)
- **Self-service portal** embedded in a native window
- **Automatic token refresh** handles hourly token rotation transparently
- **Loading screen** with Fleet logo while the portal loads
- **File download support** — downloads save to the user's Downloads folder
- **Light/dark mode** respects the user's system appearance
- **`fleet://` URL scheme** for deep linking to Self-service, Policies, Software, triggering refetches, and one-click "Update all" / "Install all" flows
- **Taskbar overlay badge** showing failing policy count (Windows equivalent of the macOS Dock badge); the page auto-reloads on reopen if the count changed while the window was closed
- **fleetd required** — both the app and installer require Fleet's orbit agent to be installed and enrolled
- **Authenticode-signed MSI installer** for secure enterprise distribution

## Requirements

- Windows 10 (version 1809 / build 17763) or newer — 64-bit
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (pre-installed on Windows 11 and recent Windows 10 builds)
- Fleet's orbit agent (fleetd) installed and enrolled
- `C:\Program Files\Orbit\bin\orbit\orbit.exe` (created by fleetd's MSI)
- `C:\Program Files\Orbit\identifier` (created by orbit on enrollment, rotates hourly)

## Installation

### From Releases

1. Download the latest `fleet_desktop-v*.msi` from the [Releases](https://github.com/allenhouchins/fleet-desktop-win/releases) page
2. Double-click the `.msi` file to run the installer
3. Follow the installation wizard

The installer requires fleetd to be installed first — it checks for `C:\Program Files\Orbit\bin\orbit\orbit.exe` before proceeding. If that file is missing, the installer aborts with an error. The app is placed in `C:\Program Files\Fleet Desktop\`. On upgrades, the installer gracefully closes Fleet Desktop before installing.

### Via Fleet (Software)

Upload the `.msi` to Fleet as a software installer. Fleet Desktop will appear in the software catalog for deployment.

### Via Intune

The MSI can be wrapped as a Win32 app and deployed via Intune. The installer's MDM-equivalent check (orbit must be installed) ensures fleetd is deployed first.

## How It Works

1. **Reads the Fleet URL** from the `Fleet osquery` Windows service's `ImagePath` registry value (parsed from the `--fleet-url` arg baked in by fleetd's MSI)
2. **Reads the device token** from `<root-dir>\identifier` — `<root-dir>` is parsed from the same service args (`--root-dir`), defaulting to `C:\Program Files\Orbit\`
3. **Opens the self-service portal** at `{FleetURL}/device/{token}/self-service` in an embedded WebView2 window

### Token Rotation

The device token in `C:\Program Files\Orbit\identifier` rotates every hour. Fleet Desktop handles this automatically:

- A background timer checks the identifier file every 60 seconds
- On HTTP 401/403 errors or error-page detection, the app immediately checks for a new token and retries (up to 3 attempts with 5-second delays)
- Token refreshes are invisible to the user — the page silently reloads with the new token

### File Downloads

When Fleet serves downloadable content, files are saved to the user's `Downloads` folder with deduplicated names (e.g., `installer (1).msi` if the original exists).

The macOS app auto-opens `.mobileconfig` MDM profiles; Windows doesn't have an equivalent file format, so all downloads are saved without auto-launch. Users can install MSIs / EXEs from their Downloads folder.

### Security

- The Fleet server URL must use HTTPS — the app refuses to start with a cleartext `http://` URL rather than send the device token over the wire unencrypted
- Device tokens are validated (alphanumerics plus `-`/`_` only) so path separators and URL metacharacters can never reach a constructed URL, and are URL-encoded on top of that
- SSO flows are pinned to the current identity-provider host: hops to a new host are only allowed via server redirects or scripted navigations (multi-host IdP chains still work), user link clicks to unrelated hosts open in the default browser, and flows expire after 10 minutes
- External links restricted to `https`, `http`, and `mailto` schemes
- Downloads keep only the final path component of the server-supplied filename and always land in the user's Downloads folder
- The `FLEET_URL` environment-variable override is compiled out of Release builds — a user-writable env var can't redirect the device token to another host
- WebView2 uses a per-launch user data folder with password autosave and autofill disabled (no cookies or cache persist between sessions); stale folders from earlier launches are swept on startup
- The single-instance named pipe is scoped to the current user's SID and restricted with `PipeOptions.CurrentUserOnly`, so other local users can't connect to or squat it
- Mutable state protected by a lock for thread safety
- The fleet:// URL scheme handler is registered machine-wide via the MSI

## Development

### Project structure

```
fleet-desktop-win/
├── FleetDesktop/
│   ├── FleetDesktop.csproj      # .NET 8 WPF project
│   ├── App.xaml / App.xaml.cs   # Application entry, single-instance, URL parsing
│   ├── MainWindow.xaml(.cs)     # WebView2 host, loading overlay, downloads
│   ├── FleetService.cs          # Config, token mgmt, badge polling, fleet:// handling
│   ├── SingleInstance.cs        # Mutex + named pipe URL dispatch
│   ├── TaskbarBadge.cs          # Overlay icon rendering
│   ├── app.manifest             # Per-monitor DPI, supportedOS
│   └── Assets/
│       ├── app.ico
│       └── fleet-logo.png
├── Installer/
│   ├── FleetDesktop.Installer.wixproj   # WiX v4 SDK project
│   ├── Product.wxs              # MSI definition
│   └── License.rtf
├── build.ps1                    # Local build script
├── sign.ps1                     # Authenticode signing (PFX or Azure Trusted Signing)
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
# Build the MSI (Release configuration)
.\build.ps1

# EXE only — skip the MSI step
.\build.ps1 -SkipMsi

# Debug build
.\build.ps1 -Configuration Debug
```

The output MSI lands at `Installer\bin\Release\fleet_desktop-v<version>.msi`.

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

| Source | Purpose |
|--------|---------|
| `HKLM\SYSTEM\CurrentControlSet\Services\Fleet osquery\ImagePath` (`--fleet-url`) | Fleet server URL — baked into the service args by fleetd's MSI |
| `HKLM\SYSTEM\CurrentControlSet\Services\Fleet osquery\ImagePath` (`--root-dir`) | Orbit root directory — used to locate `identifier` |
| `<root-dir>\identifier` | Device authentication token (rotates hourly) |

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
| `--hidden` (or `--minimized` / `/hidden`) | Start without showing the window. Used for autostart-on-login scenarios. The window appears when the user clicks the taskbar icon or a `fleet://` URL is opened. |
| `fleet://<host>` | Open directly to the given page. (Set automatically by Windows when handling a URL scheme.) |

## CI/CD

There are two GitHub Actions workflows:

### Build (`.github/workflows/build.yml`)

Runs on every push to `main` and on pull requests (doc-only changes are skipped). This is a compile-and-package check only — it uses **no secrets**, so it also passes on pull requests from forks:

1. Restores .NET dependencies (the WiX toolset comes with the wixproj's PackageReferences)
2. Publishes a self-contained, single-file EXE
3. Builds the MSI
4. Uploads the **unsigned** MSI as a workflow artifact (retained for 30 days)

### Build and Release (`.github/workflows/build-and-release.yml`)

Triggered by pushing a `v*` tag or manually via `workflow_dispatch`. Signing credentials are injected only here: the EXE and MSI are Authenticode-signed (if secrets are configured), then a GitHub Release is created with the MSI attached.

All third-party GitHub Actions in both workflows are pinned to commit SHAs (tags kept as comments) so a moved tag upstream can't silently change what runs in CI.

### Releasing a new version

1. Update `<Version>` in `FleetDesktop/FleetDesktop.csproj`
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
