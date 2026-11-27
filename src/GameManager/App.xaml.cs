// Entry point: dispatches the different modes (service / admin tools / protocol URLs / UI),
// registers the steam:// protocol best-effort and opens the launcher window.

using System;
using System.Linq;
using System.Windows;

namespace GameManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string[] args = e.Args;

        // Admin modes used by the Protocol menu (launched via UAC).
        if (args.Any(a => a.Equals("--register-all", StringComparison.OrdinalIgnoreCase)))
        {
            Launcher.RegisterAllUsers();
            Shutdown();
            return;
        }
        if (args.Any(a => a.Equals("--unregister-all", StringComparison.OrdinalIgnoreCase)))
        {
            Launcher.UnregisterAllUsers();
            Shutdown();
            return;
        }
        if (args.Any(a => a.Equals("--install-service", StringComparison.OrdinalIgnoreCase)))
        {
            ServiceHost.Install();
            Shutdown();
            return;
        }
        if (args.Any(a => a.Equals("--uninstall-service", StringComparison.OrdinalIgnoreCase)))
        {
            ServiceHost.Uninstall();
            Shutdown();
            return;
        }

        // The Windows service body (SCM calls the exe with this argument; blocks here).
        if (args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase)))
        {
            ServiceHost.Run();
            Shutdown();
            return;
        }

        bool forceFullScreen = args.Any(a => a.Equals("--fullscreen", StringComparison.OrdinalIgnoreCase));

        // Spawned by the service inside a user session: show UI only, forced to the front.
        if (args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)))
        {
            // One instance per user: if it is already running, ask it to come forward.
            if (!SingleInstance.Acquire(OnActivateRequest))
            {
                Shutdown();
                return;
            }

            bool fs = forceFullScreen || Config.Current.StartFullScreen;
            var w = new MainWindow(forceFront: true) { StartFullScreen = fs };
            w.Show();
            MainWindow = w;
            w.Closed += (_, _) => Shutdown();
            return;
        }

        // Handle a steam:// URL passed by the shell.
        if (Launcher.TryParseUrl(args, out string? gameName))
        {
            if (!string.IsNullOrEmpty(gameName))
            {
                if (Launcher.TryLaunchByName(gameName))
                {
                    Shutdown();
                    return;
                }
                Launcher.Report(Strings.MsgGameNotFound(gameName), MessageBoxImage.Information);
            }
            else
            {
                // "steam://open/bigpicture": ask the SYSTEM service to show an elevated
                // Game Manager in this session. Falls back to a local window below
                // when the service is not installed.
                if (ServiceHost.TrySignalShow())
                {
                    Shutdown();
                    return;
                }
            }
        }

        // One instance per user (also covers the no-service fallback path).
        if (!SingleInstance.Acquire(OnActivateRequest))
        {
            Shutdown();
            return;
        }

        // Normal start: register best-effort (HKLM when elevated, otherwise HKCU).
        Launcher.RegisterBestEffort();

        var window = new MainWindow { StartFullScreen = forceFullScreen || Config.Current.StartFullScreen };
        window.Show();
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
    }

    /// <summary>Another instance asked this one to come to the front.</summary>
    private void OnActivateRequest()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is Window w)
            {
                w.WindowState = WindowState.Minimized == w.WindowState ? WindowState.Normal : w.WindowState;
                w.Topmost = true;
                w.Activate();
                w.Topmost = false;
            }
        });
    }
}