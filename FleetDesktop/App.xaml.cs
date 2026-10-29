using System;
using System.Windows;

namespace FleetDesktop;

/// <summary>
/// WPF application entry. Mirrors the macOS FleetDesktopMain + AppDelegate:
/// - Routes subcommands (<c>notify</c>, <c>help</c>) before any GUI state exists.
/// - Enforces a single GUI instance (mutex + named pipe to forward fleet:// URLs).
/// - Starts <see cref="FleetService"/>, which owns config, token rotation, and the browser window.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private FleetService? _fleetService;

    /// <summary>
    /// Whether the app was launched with a "hidden" flag (e.g. autostart on login).
    /// Equivalent to the macOS "open -j" / hidden login item case: window stays closed
    /// until the user explicitly foregrounds the app or a fleet:// URL arrives.
    /// </summary>
    public bool LaunchedHidden { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Dispatched before the single-instance guard is installed, so a subcommand
        // never touches the mutex, the fleet:// pipe or FleetService.
        switch (Cli.RouteArgs(e.Args))
        {
            case Cli.Route.Usage usage:
                Cli.Emit(usage.Text, toStderr: usage.Code != 0);
                Environment.Exit(usage.Code);
                return;

            case Cli.Route.Notify notify:
                if (notify.Options.IsDetachedChild)
                {
                    // Creates the toast and returns; the dispatcher loop drives it.
                    NotifyCommand.RunChild(notify.Options);
                }
                else
                {
                    NotifyCommand.RunParent(notify.Options); // never returns
                }
                return;

            case Cli.Route.RunGui gui:
                StartGui(gui.FleetUrl, gui.Hidden);
                return;
        }
    }

    private void StartGui(string? initialUrl, bool hidden)
    {
        LaunchedHidden = hidden;

        // v2: the pipe name gained a per-user SID suffix and the protocol gained
        // the "reopen" message. Versioning the channel name keeps a leftover
        // pre-upgrade instance from owning the mutex while newer binaries forward
        // messages to a pipe nobody is listening on.
        _singleInstance = new SingleInstance("FleetDesktop.Singleton.v2");
        if (!_singleInstance.AcquireOrForward(initialUrl, requestReopenIfNoUrl: !hidden))
        {
            // Another instance is running; we've forwarded the URL (or a reopen
            // request) to it. Exit quietly.
            Shutdown(0);
            return;
        }

        _singleInstance.OnMessageReceived += message =>
        {
            // Forwarded from a second-instance launch — dispatch on the UI thread.
            Dispatcher.BeginInvoke(() =>
            {
                if (message == SingleInstance.ReopenMessage)
                {
                    // Plain relaunch (Start Menu, taskbar) with no URL: show the window.
                    _fleetService?.ShowWindow();
                }
                else
                {
                    _fleetService?.HandleFleetUrl(message);
                }
            });
        };

        _fleetService = new FleetService();
        _fleetService.Run(initialUrl, hidden);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _fleetService?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
