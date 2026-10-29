using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using static FleetDesktop.NativeMethods;

namespace FleetDesktop;

/// <summary>
/// The <c>notify</c> subcommand. Returns as soon as the toast is on screen, but a
/// window dies with its process — so a second process owns it:
///
///     FleetDesktop notify                 parent; its exit code is what the caller records
///       │  ...handshake pipe...
///       └─ FleetDesktop notify --detached-child     owns the window
///
/// Port of NotifyCommand.swift. One Windows-specific step: fleetd runs scripts as
/// SYSTEM in session 0, where no window can be seen, so when the parent finds itself
/// there it launches the child in the console user's session (the equivalent of the
/// macOS script's <c>launchctl asuser</c>).
/// </summary>
internal static class NotifyCommand
{
    /// <summary>How long the parent waits for the child's line. Longer than the child's
    /// load deadline, since reporting a load failure legitimately takes that long.</summary>
    private static readonly TimeSpan HandshakeTimeout = ToastWindow.LoadTimeout + TimeSpan.FromSeconds(15);

    private const int MaxHandshakeLength = 4096;

    /// <summary>One toast per session at a time. A named mutex in the session-local
    /// namespace: released by the kernel on any exit, crashes included, so a stale
    /// lock can never block the next toast.</summary>
    public const string ToastLockName = @"Local\FleetDesktop.notify.lock";

    /// <summary>Held for the child's whole lifetime.</summary>
    private static Mutex? _toastLock;

    // ---- Parent ----------------------------------------------------------------

    /// <summary>Runs the parent half. Never returns: exits with the outcome.</summary>
    public static void RunParent(NotifyOptions options)
    {
        // fleetd's script runner is a SYSTEM service in session 0. Nothing drawn there
        // is visible, so the toast has to be created in the console user's session.
        SafeAccessTokenHandle? userToken = null;
        if (Process.GetCurrentProcess().SessionId == 0)
        {
            var consoleSession = WTSGetActiveConsoleSessionId();
            if (consoleSession == InvalidSessionId)
            {
                Report(ExitCode.NoConsoleUser, "No user is logged in at the console.");
            }
            if (!WTSQueryUserToken(consoleSession, out var rawToken))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ERROR_NO_TOKEN)
                {
                    Report(ExitCode.NoConsoleUser, "No user is logged in at the console.");
                }
                Report(ExitCode.InternalError, $"Could not get the console user's token (error {error}).");
            }
            userToken = new SafeAccessTokenHandle(rawToken);
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            Report(ExitCode.InternalError, "Could not determine our own executable path.");
        }

        AnonymousPipeServerStream pipe;
        try
        {
            pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        }
        catch (Exception ex)
        {
            Report(ExitCode.InternalError, $"Could not create the handshake pipe: {ex.Message}");
            return;
        }

        using (pipe)
        using (userToken)
        {
            // AbsoluteUri is fully percent-encoded, so it can't contain a quote.
            var commandLine = new StringBuilder()
                .Append('"').Append(executable).Append('"')
                .Append(" notify --url \"").Append(options.Url.AbsoluteUri).Append('"')
                .Append(" --detached-child --handshake-handle ").Append(pipe.GetClientHandleAsString());

            try
            {
                DetachedProcess.Start(executable!, commandLine, userToken, pipe.ClientSafePipeHandle);
            }
            catch (Exception ex)
            {
                Report(ExitCode.InternalError, $"Could not start the toast process: {ex.Message}");
            }

            // Our copy of the write end must go, or the read below never sees EOF.
            pipe.DisposeLocalCopyOfClientHandle();

            var line = ReadLine(pipe, HandshakeTimeout);
            if (line == null || Handshake.Decode(line) is not { } outcome)
            {
                Report(ExitCode.InternalError, "The toast process exited without reporting.");
                return;
            }
            Report(outcome.Code, outcome.Message);
        }
    }

    /// <summary>
    /// Reads one line, or gives up after <paramref name="timeout"/>.
    ///
    /// The deadline is what keeps a wedged child from hanging the caller: every other
    /// bound lives on the child's UI thread, which is exactly what freezes if WebView2
    /// stalls. Returns null on timeout, EOF before a newline, or an over-long line —
    /// all of which the caller reports as an internal error.
    /// </summary>
    private static string? ReadLine(Stream stream, TimeSpan timeout)
    {
        var read = Task.Run(() =>
        {
            var data = new MemoryStream();
            var buffer = new byte[1];
            while (true)
            {
                int count;
                try
                {
                    count = stream.Read(buffer, 0, 1);
                }
                catch
                {
                    return null;
                }
                if (count <= 0) break; // EOF
                if (buffer[0] == (byte)'\n') break;
                // Checked before appending, so an over-long line is rejected rather
                // than silently truncated into something that still decodes.
                if (data.Length >= MaxHandshakeLength) return null;
                data.WriteByte(buffer[0]);
            }
            return data.Length == 0 ? null : Encoding.UTF8.GetString(data.ToArray());
        });

        try
        {
            return read.Wait(timeout) ? read.Result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The only exit path in the parent.</summary>
    private static void Report(ExitCode code, string message)
    {
        Cli.Emit(message, toStderr: code != ExitCode.Displayed);
        Environment.Exit((int)code);
    }

    // ---- Child ------------------------------------------------------------------

    /// <summary>
    /// Runs the child half: takes the toast lock, checks that someone can see the
    /// screen, then creates the toast and returns so the WPF dispatcher drives it.
    /// Exits the process directly for every failure.
    /// </summary>
    public static void RunChild(NotifyOptions options)
    {
        Handshake handshake;
        try
        {
            handshake = new Handshake(options.HandshakeHandle!);
        }
        catch (Exception ex)
        {
            NotifyLog.Warn($"could not open the handshake pipe: {ex.Message}");
            Environment.Exit((int)ExitCode.InternalError);
            return;
        }

        // Never log the URL: the server embeds the device token in its path.
        NotifyLog.Info($"target host {options.Url.Host}");

        switch (AcquireToastLock())
        {
            case ToastLockResult.Acquired:
                break;
            case ToastLockResult.HeldElsewhere:
                handshake.Send(ExitCode.AnotherDisplayed, "Another notification is already displayed.");
                Environment.Exit((int)ExitCode.AnotherDisplayed);
                return;
            default:
                handshake.Send(ExitCode.InternalError, "Could not take the notification lock.");
                Environment.Exit((int)ExitCode.InternalError);
                return;
        }

        // Behind a locked screen the toast would expire unseen while reporting success.
        if (IsScreenLocked())
        {
            handshake.Send(ExitCode.ScreenLocked, "The screen is locked.");
            Environment.Exit((int)ExitCode.ScreenLocked);
            return;
        }

        if (GetSystemMetrics(SM_CMONITORS) <= 0)
        {
            handshake.Send(ExitCode.NoDisplay, "No display is attached.");
            Environment.Exit((int)ExitCode.NoDisplay);
            return;
        }

        new ChildController(options.Url, handshake).Start();
    }

    private enum ToastLockResult
    {
        Acquired,
        HeldElsewhere,
        Failed,
    }

    /// <summary>Taken before the page loads, so a toast that is still loading counts as
    /// displayed.</summary>
    private static ToastLockResult AcquireToastLock()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: false, ToastLockName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Previous holder crashed — the lock is ours.
                acquired = true;
            }
            if (!acquired)
            {
                mutex.Dispose();
                return ToastLockResult.HeldElsewhere;
            }
            _toastLock = mutex;
            return ToastLockResult.Acquired;
        }
        catch (Exception ex)
        {
            NotifyLog.Warn($"toast lock failed: {ex.Message}");
            return ToastLockResult.Failed;
        }
    }

    /// <summary>Whether the current session is at the lock screen. An unanswerable
    /// query means unlocked rather than a failure.</summary>
    private static bool IsScreenLocked()
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WTS_CURRENT_SESSION, WTSSessionInfoEx, out var buffer, out var length))
        {
            return false;
        }
        try
        {
            if (buffer == IntPtr.Zero || length < Marshal.SizeOf<WTSINFOEX>()) return false;
            var info = Marshal.PtrToStructure<WTSINFOEX>(buffer);
            return info.Level == 1 && info.Data.SessionFlags == WTS_SESSIONSTATE_LOCK;
        }
        catch
        {
            return false;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>Owns the toast for the lifetime of the child process. Port of the macOS
    /// ChildDelegate.</summary>
    private sealed class ChildController
    {
        private readonly Uri _url;
        private readonly Handshake _handshake;
        private ToastWindow? _toast;
        private Timer? _watchdog;

        /// <summary>Guards <see cref="_didReport"/>. The watchdog fires on a thread-pool
        /// thread, so the flag is reachable from two threads.</summary>
        private readonly object _reportLock = new();
        private bool _didReport;

        public ChildController(Uri url, Handshake handshake)
        {
            _url = url;
            _handshake = handshake;
        }

        public void Start()
        {
            var toast = new ToastWindow(_url);
            _toast = toast;
            toast.Displayed += ReportDisplayed;
            toast.Finished += HandleFinish;
            ArmWatchdog();
            toast.Present();
        }

        /// <summary>
        /// Last resort: the toast has no title bar, no close button and no Esc handling,
        /// so a wedged WebView2 would leave an undismissable window floating over
        /// everything. Deliberately NOT on the UI thread: a frozen UI thread is the case
        /// this exists to catch.
        /// </summary>
        private void ArmWatchdog()
        {
            var limit = ToastWindow.WatchdogLimit;
            _watchdog = new Timer(
                _ => Finish(ExitCode.InternalError, $"Watchdog fired after {(int)limit.TotalSeconds}s."),
                null, limit, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Claims the right to send the handshake line. False if already sent.</summary>
        private bool ClaimReport()
        {
            lock (_reportLock)
            {
                if (_didReport) return false;
                _didReport = true;
                return true;
            }
        }

        /// <summary>The toast is up. Let the parent exit, but keep running.</summary>
        private void ReportDisplayed()
        {
            if (!ClaimReport()) return;
            _handshake.Send(ExitCode.Displayed, "Notification displayed.");
        }

        private void HandleFinish(ToastOutcome outcome)
        {
            switch (outcome)
            {
                case ToastOutcome.PrimaryAction primary:
                    NotifyLog.Info($"primary action{(primary.Id != null ? $" id={primary.Id}" : "")}");
                    ExitChild(ExitCode.Displayed);
                    break;
                case ToastOutcome.Dismissed dismissed:
                    NotifyLog.Info($"dismissed{(dismissed.Reason != null ? $" reason={dismissed.Reason}" : "")}");
                    ExitChild(ExitCode.Displayed);
                    break;
                case ToastOutcome.TimedOut:
                    NotifyLog.Info("display timeout expired");
                    ExitChild(ExitCode.Displayed);
                    break;
                case ToastOutcome.LoadFailed failed:
                    Finish(ExitCode.LoadFailed, $"Page did not load: {failed.Detail}");
                    break;
                case ToastOutcome.HttpError http:
                    Finish(ExitCode.HttpError, $"Page returned HTTP {http.Status}.");
                    break;
                case ToastOutcome.ContentError content:
                    Finish(ExitCode.HttpError, content.Detail);
                    break;
                case ToastOutcome.NoDisplay:
                    Finish(ExitCode.NoDisplay, "No display is attached.");
                    break;
            }
        }

        /// <summary>Reports, if the parent is still waiting, and exits.</summary>
        private void Finish(ExitCode code, string message)
        {
            if (ClaimReport())
            {
                _handshake.Send(code, message);
            }
            NotifyLog.Info($"finished: {message} code={(int)code}");
            Environment.Exit((int)code);
        }

        /// <summary>
        /// Exits after a user action or the display timeout. Normally the toast was
        /// already reported, so this status only reaches the logs. But a page can post
        /// <c>primary</c> or <c>dismiss</c> before it posts <c>ready</c>, in which case
        /// nothing has been reported yet and exiting silently would leave the parent
        /// reading EOF and calling it an internal error.
        /// </summary>
        private void ExitChild(ExitCode code)
        {
            _watchdog?.Dispose();
            if (ClaimReport())
            {
                _handshake.Send(code, "Notification closed before it was displayed.");
            }
            Environment.Exit((int)code);
        }
    }
}

/// <summary>
/// The parent/child channel: one line, once. Kept trivial to parse so a partial line
/// cannot be misread as a different outcome.
/// </summary>
internal sealed class Handshake : IDisposable
{
    private readonly AnonymousPipeClientStream _pipe;

    public Handshake(string inheritedHandle)
    {
        _pipe = new AnonymousPipeClientStream(PipeDirection.Out, inheritedHandle);
    }

    public void Send(ExitCode code, string message)
    {
        // Strip newlines so the message can't forge extra lines.
        var sanitized = message.Replace('\r', ' ').Replace('\n', ' ');
        var bytes = Encoding.UTF8.GetBytes($"{(int)code} {sanitized}\n");
        try
        {
            _pipe.Write(bytes, 0, bytes.Length);
            _pipe.Flush();
        }
        catch
        {
            // The parent has already gone (it closes the pipe once it has our line).
        }
    }

    public static (ExitCode Code, string Message)? Decode(string line)
    {
        var separator = line.IndexOf(' ');
        var first = separator < 0 ? line : line[..separator];
        if (!int.TryParse(first, out var raw) || !Enum.IsDefined(typeof(ExitCode), raw))
        {
            return null;
        }
        var message = separator < 0 ? "" : line[(separator + 1)..];
        return ((ExitCode)raw, message);
    }

    public void Dispose() => _pipe.Dispose();
}

/// <summary>
/// Starts the toast child with raw CreateProcess so that (a) it can be placed in the
/// console user's session when the parent runs as SYSTEM, and (b) exactly one handle —
/// the handshake pipe — is inherited. The .NET Process class inherits every inheritable
/// handle in the parent, which would include the caller's stdout pipe; fleetd reads
/// script output until EOF, so a child holding that pipe open would make the script
/// look hung for the toast's whole lifetime. The child's own stdio goes to NUL.
/// </summary>
internal static class DetachedProcess
{
    public static void Start(string executable, StringBuilder commandLine, SafeAccessTokenHandle? userToken, SafeHandle inheritedHandle)
    {
        var inheritable = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = 1,
        };
        using var nul = CreateFileW("NUL", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            ref inheritable, OPEN_EXISTING, 0, IntPtr.Zero);
        if (nul.IsInvalid)
        {
            throw new IOException($"CreateFile(NUL) failed (error {Marshal.GetLastWin32Error()}).");
        }

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributeList = Marshal.AllocHGlobal(size);
        var handles = new[] { nul.DangerousGetHandle(), inheritedHandle.DangerousGetHandle() };
        var pinnedHandles = GCHandle.Alloc(handles, GCHandleType.Pinned);
        var environment = IntPtr.Zero;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                throw new IOException($"InitializeProcThreadAttributeList failed (error {Marshal.GetLastWin32Error()}).");
            }
            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    pinnedHandles.AddrOfPinnedObject(), (IntPtr)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
            {
                throw new IOException($"UpdateProcThreadAttribute failed (error {Marshal.GetLastWin32Error()}).");
            }

            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    // The interactive desktop of whichever session the token belongs to.
                    lpDesktop = @"winsta0\default",
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = nul.DangerousGetHandle(),
                    hStdOutput = nul.DangerousGetHandle(),
                    hStdError = nul.DangerousGetHandle(),
                },
                lpAttributeList = attributeList,
            };

            var token = userToken?.DangerousGetHandle() ?? IntPtr.Zero;
            if (token != IntPtr.Zero && !CreateEnvironmentBlock(out environment, token, false))
            {
                throw new IOException($"CreateEnvironmentBlock failed (error {Marshal.GetLastWin32Error()}).");
            }

            const uint baseFlags = CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT | CREATE_NEW_PROCESS_GROUP;
            // Detach from any job the caller put us in (a kill-on-close job would take
            // the toast down with the script). Jobs that forbid breakaway refuse it, so
            // fall back to a plain launch.
            if (!Launch(token, executable, commandLine, baseFlags | CREATE_BREAKAWAY_FROM_JOB, environment, ref startup, out var error) &&
                !(error == ERROR_ACCESS_DENIED && Launch(token, executable, commandLine, baseFlags, environment, ref startup, out error)))
            {
                throw new IOException($"CreateProcess failed (error {error}).");
            }
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            pinnedHandles.Free();
            GC.KeepAlive(inheritedHandle);
            GC.KeepAlive(userToken);
        }
    }

    private static bool Launch(IntPtr token, string executable, StringBuilder commandLine, uint flags, IntPtr environment,
        ref STARTUPINFOEX startup, out int error)
    {
        // CreateProcess may modify the command line buffer, so hand it a copy.
        var buffer = new StringBuilder(commandLine.ToString());
        PROCESS_INFORMATION info;
        bool ok = token == IntPtr.Zero
            ? CreateProcessW(executable, buffer, IntPtr.Zero, IntPtr.Zero, true, flags, environment, null, ref startup, out info)
            : CreateProcessAsUserW(token, executable, buffer, IntPtr.Zero, IntPtr.Zero, true, flags, environment, null, ref startup, out info);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        if (ok)
        {
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
        }
        return ok;
    }
}
