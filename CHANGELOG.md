## Fleet Desktop for Windows v1.2.0 (October 29, 2026)

Brings the Windows app level with Fleet Desktop for macOS v1.6.0, and tightens a few Windows-specific edges found in a security and performance review.

### Fixed

* The Fleet URL and orbit directory are now read from the `Fleet osquery` service's `Environment` value (`ORBIT_FLEET_URL`, `ORBIT_ROOT_DIR`), which is where fleetd MSIs built since September 2026 put them. Hosts enrolled with older MSIs, which pass `--fleet-url` in the service's `ImagePath`, keep working. Previously the app only understood the older form and failed to start against a current fleetd.
* A scheme-less Fleet URL such as `fleet.example.com:8080` is treated as HTTPS, matching orbit. A Fleet URL without a host is rejected instead of building a device URL whose host is the literal path segment `device`.
* The taskbar badge counts only failing policies shown to the end user, ignoring policies the IT admin hid (falls back to the total on servers older than that feature).
* "Read about transparency" and "About Fleet" links open in the default browser. The app resolves the transparency endpoint's redirect itself, so the device token never reaches the browser's address bar or history, and the external page no longer renders inside Fleet Desktop with no way back.
* Okta FastPass sign-in when Fleet Desktop SSO is enabled: Okta launches Okta Verify through its `com-okta-authenticator` URL scheme, which the in-app browser now hands to the app instead of abandoning the sign-in.
* A navigation to the Fleet host over cleartext `http://` is refused rather than sending the device token unencrypted.

### Added

* `FleetDesktop.exe notify --url <https url>` shows Fleet's end-user patch notification as a toast anchored bottom-right of the screen, with the same exit-code contract as the macOS app (0 displayed, 2 usage, 30 load failed, 31 HTTP/content error, 40 no console user, 41 screen locked, 42 no display, 50 another notification displayed, 70 internal error). When invoked by fleetd's script runner as SYSTEM, the app itself launches the toast in the console user's session. Ctrl+Shift+X closes a toast whose page's own dismiss doesn't work. The installer advertises support under `HKLM\Software\FleetDM\FleetDesktop\Capabilities\notify`.
* Windows on ARM: the build produces `fleet_desktop-v<version>-x64.msi` and `fleet_desktop-v<version>-arm64.msi`, matching fleetd's own ARM64 support and the macOS universal binary.
* The window remembers its position and size between launches.
* The title bar and loading screen follow Windows dark mode.
* A hidden launch (`--hidden`) now truly preloads the page, so the first open is instant.

### Changed

* Native libraries ship next to the EXE in `Program Files` instead of being extracted to the user's `%TEMP%` on every launch. The single-file bundle is no longer compressed, so assemblies are memory-mapped from disk rather than inflated into private memory, and the app's code is precompiled (ReadyToRun). Startup is faster and nothing is loaded from a user-writable directory.
* While the window is hidden, the WebView2 renderer is suspended, so an idle Fleet Desktop no longer runs page timers in the background.
* The single-instance pipe caps the message it accepts at 8 KB.
* WebView2's web-message channel is closed in the main window, which never uses it.

## Fleet Desktop for Windows v1.1.0

* Feature parity with the macOS app as of July 2026, plus security and speed review fixes.

## Fleet Desktop for Windows v1.0.0

* Initial release.
