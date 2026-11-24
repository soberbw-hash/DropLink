using System.IO;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace DropLink;
public partial class App : System.Windows.Application
{
    private SingleInstance? singleInstance;
    // True only for the process that actually owns the receiver chain, so a
    // second launch that immediately shuts down never kills the live backends.
    private bool ownsChain;

    internal static bool StartMinimized { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartMinimized = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        AppLog.Mark("app:start (tray=" + StartMinimized + ", elevated=" + Elevation.IsElevated() + ")");
        // Self-contained: derive backend/driver paths even if launched by
        // double-clicking the exe with no launcher environment at all.
        AppPaths.EnsureEnvironment();

        // A portable app that can only be started from a .cmd does not read as
        // a real program. The shortcut is written by the shell itself, so it is
        // an ordinary .lnk that Explorer, the Start menu and "pin to taskbar"
        // all accept - a hand-built file is rejected by half of them.
        EnsureDesktopShortcut();

        // Driver binding needs an administrator token. The manifest already
        // asks for one, but if it was bypassed (shortcut, filtered parent) the
        // app would silently fail at the first click. Relaunch once, elevated.
        // The guard is a command-line flag because environment variables do not
        // survive elevation.
        const string retryFlag = "--elevated-retry";
        if (!Elevation.IsElevated() && !e.Args.Contains(retryFlag, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var exe = Environment.ProcessPath
                          ?? Path.Combine(AppContext.BaseDirectory, "DropLink.exe");
                var psi = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                foreach (var arg in e.Args) psi.ArgumentList.Add(arg);
                if (!e.Args.Contains(retryFlag, StringComparer.OrdinalIgnoreCase))
                    psi.ArgumentList.Add(retryFlag);
                System.Diagnostics.Process.Start(psi);
                Shutdown();
                return;
            }
            catch
            {
                // Elevation refused: keep running so the UI can explain it.
            }
        }

        // A second launch should bring the existing window back, not start a
        // second receiver. The named event wakes the first instance.
        //
        // Send, not ApplicationIdle: the idle queue only drains when nothing
        // else is pending, and a receiver that is busy starting a chain can go
        // minutes without draining it. That is how double-clicking the icon
        // produced nothing while the app was in fact running.
        singleInstance = new SingleInstance(() =>
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(ShowExistingWindow)));
        if (!singleInstance.IsFirst)
        {
            Shutdown();
            return;
        }
        // We are the only receiver in this session, so any backend still running
        // is a leftover from a run that never cleaned up. It would hold the radio
        // and the stop event and make every start fail.
        ownsChain = true;
        try
        {
            var killed = BackendChain.KillStaleChain();
            if (killed > 0) AppLog.Write($"cleaned {killed} stale backend process(es) from a previous run");
        }
        catch (Exception error) { AppLog.Write("stale chain cleanup failed: " + error.Message); }

        // Last-resort crash guard: never die silently. async void handlers in
        // WPF can throw outside try/catch; show the reason instead of vanishing.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "DropLink 遇到问题：\n" + args.Exception.Message +
                "\n\n（详细信息：" + args.Exception.GetType().Name + "）",
                "DropLink 异常", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "DropLink 遇到严重问题：\n" + (args.ExceptionObject as System.Exception)?.Message,
                "DropLink 严重异常", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        if (StartMinimized)
        {
            // The window is never shown, so Loaded never fires: kick the guide
            // and the receiver off explicitly or a tray-only launch does nothing.
            _ = window.InitializeAsync();
        }
        else
        {
            window.Show();
        }
    }

    // Written once, and never overwritten: a shortcut the user renamed or moved
    // is theirs, and silently recreating it every launch would be its own kind
    // of rude. Failure is logged and ignored - the receiver still has to work.
    private static void EnsureDesktopShortcut()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop)) return;
            var link = Path.Combine(desktop, "DropLink.lnk");
            if (File.Exists(link)) return;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.IconLocation = exe + ",0";
            shortcut.Description = "DropLink - iPhone 隔空投送接收器";
            shortcut.Save();
            AppLog.Write("desktop shortcut created: " + link);
        }
        catch (Exception error)
        {
            AppLog.Write("desktop shortcut failed: " + error.Message);
        }
    }

    // A wake-up can beat the window construction by a few milliseconds at
    // logon, so a missing window is retried instead of silently dropped. The
    // whole point of the signal is that the user ends up with something on
    // screen; a wake-up that does nothing is indistinguishable from a hang.
    private void ShowExistingWindow()
    {
        if (MainWindow is MainWindow ready)
        {
            ready.ShowFromTray();
            return;
        }
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100);
                var window = Dispatcher.Invoke(() => MainWindow as MainWindow);
                if (window is null) continue;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(window.ShowFromTray));
                return;
            }
            AppLog.Write("activate requested but no window exists yet");
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Logoff/shutdown does not go through the tray exit path, so the chain
        // would otherwise be orphaned and block the next launch.
        if (ownsChain)
        {
            try { BackendChain.KillStaleChain(); } catch (Exception) { /* best-effort */ }
            // Closing the job is the kernel-level guarantee: anything that
            // somehow survived the explicit cleanup dies here too.
            try { ProcessJob.Shared?.Dispose(); } catch (Exception) { /* best-effort */ }
        }
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
