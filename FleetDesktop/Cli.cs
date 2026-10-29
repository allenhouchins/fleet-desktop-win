using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FleetDesktop;

/// <summary>
/// Exit codes for <c>notify</c>. The Fleet server maps these to activities, so the values
/// must stay stable and identical to the macOS app's. Bands: 30s server/network, 40s
/// nobody was there to see it, 50s another notification is in the way.
///
/// 1 is unassigned, and nothing uses 126-165 (shell-reserved; 127 is what a caller sees
/// when the binary is missing). The calling script owns 100 and 101 (not installed /
/// no notify support). On macOS the script also owns 40; on Windows the script runs as
/// SYSTEM in session 0, so the app performs the "is anyone logged in" check itself and
/// reports 40 with the same meaning.
/// </summary>
internal enum ExitCode
{
    Displayed = 0,
    Usage = 2,
    LoadFailed = 30,
    HttpError = 31,
    NoConsoleUser = 40,
    ScreenLocked = 41,
    NoDisplay = 42,
    AnotherDisplayed = 50,
    InternalError = 70,
}

/// <param name="Url">Page to display. The server builds this, including the device token.</param>
/// <param name="IsDetachedChild">Set on the re-launched child that owns the window.</param>
/// <param name="HandshakeHandle">Inherited write handle of the handshake pipe, as a
/// decimal string (see <see cref="System.IO.Pipes.AnonymousPipeServerStream.GetClientHandleAsString"/>).
/// Child only.</param>
internal sealed record NotifyOptions(Uri Url, bool IsDetachedChild, string? HandshakeHandle);

/// <summary>
/// Command-line routing. Port of cli.swift from the macOS app, plus the Windows-only
/// GUI arguments (<c>--hidden</c> and a <c>fleet://</c> URL handed over by the shell).
/// </summary>
internal static class Cli
{
    public const string UsageLine = "Usage: FleetDesktop notify --url <https url>";

    internal abstract record Route
    {
        /// <summary>Normal GUI launch, optionally hidden or with a fleet:// deep link.</summary>
        public sealed record RunGui(string? FleetUrl, bool Hidden) : Route;

        public sealed record Notify(NotifyOptions Options) : Route;

        /// <summary>Print <paramref name="Text"/> and exit with <paramref name="Code"/>.</summary>
        public sealed record Usage(string Text, int Code) : Route;
    }

    /// <summary>
    /// Decides what this invocation means, given the arguments after the executable.
    ///
    /// Dash-prefixed flags fall through to the GUI on purpose (<c>--hidden</c> is one of
    /// them, and launchers may inject arguments the app never asked for). A
    /// <c>fleet://</c> URL is what the shell passes when handling the URL scheme. Only a
    /// bare non-dash word is a subcommand.
    /// </summary>
    public static Route RouteArgs(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new Route.RunGui(null, false);
        }

        var first = args[0].Trim();
        if (first == "notify")
        {
            return ParseNotify(args, 1);
        }
        // Caught before the fall-through below, so typing --help doesn't open the GUI.
        if (first is "help" or "--help" or "-h" or "/?")
        {
            return new Route.Usage(UsageLine, 0);
        }

        string? url = null;
        var hidden = false;
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var arg = raw.Trim();
            if (arg.Equals("--hidden", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/hidden", StringComparison.OrdinalIgnoreCase))
            {
                hidden = true;
            }
            else if (arg.StartsWith("fleet://", StringComparison.OrdinalIgnoreCase))
            {
                url ??= arg;
            }
            else if (arg.StartsWith('-') || arg.StartsWith('/'))
            {
                // Unknown flag: ignored, like the macOS app ignores -psn_ arguments.
            }
            else
            {
                return new Route.Usage($"Unknown subcommand '{arg}'.\n{UsageLine}", (int)ExitCode.Usage);
            }
        }
        return new Route.RunGui(url, hidden);
    }

    private static Route ParseNotify(IReadOnlyList<string> args, int start)
    {
        Uri? url = null;
        var isDetachedChild = false;
        string? handshakeHandle = null;

        var index = start;
        while (index < args.Count)
        {
            var flag = args[index];
            var next = index + 1 < args.Count ? args[index + 1] : null;

            switch (flag)
            {
                case "--url":
                    if (next == null) return UsageError($"{flag} requires a value.");
                    // https only: the URL carries the device token. A host is required
                    // too, since Uri accepts host-less values like "https://".
                    if (!Uri.TryCreate(next, UriKind.Absolute, out var parsed) ||
                        !string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(parsed.Host))
                    {
                        return UsageError("--url must be an https URL with a host.");
                    }
                    url = parsed;
                    index += 2;
                    break;

                case "--detached-child":
                    isDetachedChild = true;
                    index += 1;
                    break;

                case "--handshake-handle":
                    if (next == null) return UsageError($"{flag} requires a value.");
                    if (!long.TryParse(next, out var handleValue) || handleValue <= 0)
                    {
                        return UsageError("--handshake-handle must be a positive integer.");
                    }
                    handshakeHandle = next;
                    index += 2;
                    break;

                default:
                    return UsageError($"Unknown option '{flag}' for notify.");
            }
        }

        if (url == null)
        {
            return UsageError("notify requires --url.");
        }
        // The child is spawned by the parent, never by a person. Without the pipe it
        // cannot report its outcome, which would strand the parent.
        if (isDetachedChild && handshakeHandle == null)
        {
            return UsageError("--detached-child requires --handshake-handle.");
        }

        return new Route.Notify(new NotifyOptions(url, isDetachedChild, handshakeHandle));
    }

    private static Route UsageError(string message) =>
        new Route.Usage(message + "\n" + UsageLine, (int)ExitCode.Usage);

    // ---- Output --------------------------------------------------------------

    private static bool _consoleAttached;

    /// <summary>
    /// Writes a line to stdout or stderr. The app is a GUI-subsystem executable, so it
    /// has no console of its own: when a script runs it with redirected output the
    /// inherited handles are used as-is, and when a person runs it from a terminal we
    /// attach to that terminal so the text has somewhere to go.
    /// </summary>
    public static void Emit(string text, bool toStderr = false)
    {
        if (!_consoleAttached)
        {
            _consoleAttached = true;
            try { AttachConsole(AttachParentProcess); } catch { /* no parent console */ }
        }
        try
        {
            var writer = toStderr ? Console.Error : Console.Out;
            writer.Write(text.EndsWith('\n') ? text : text + "\n");
            writer.Flush();
        }
        catch
        {
            // Nowhere to write; the exit code still carries the outcome.
        }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
