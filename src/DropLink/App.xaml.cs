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
    // Set once a window exists. Read by the startup watchdog and by the
    // single-instance handshake, so it is volatile rather than merely internal.
    private volatile bool windowReady;

    internal static bool StartMinimized { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartMinimized = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        AppLog.Mark("app:start (tray=" + StartMinimized + ", elevated=" + Elevation.IsElevated() + ")");
        // Self-contained: derive backend/driver paths even if launched by
        // double-clicking the exe with no launcher environment at all.
        AppPaths.EnsureEnvironment();
        // Before any window or tray icon exists: the shell reads this when the
        // first window is created, and a later call would leave the taskbar and
        // notifications on the path-derived identity (with its stale icon).
        AppIdentity.Apply();

        // Dev-only: render the settings page to a PNG and exit, so a layout
        // regression is visible without clicking through the app. Placed before
        // the single-instance and stale-chain logic on purpose: a preview must
        // not disturb a receiver that is already running.
        if (TryPreviewMain(e.Args)) { Shutdown(); return; }
        if (TryPreviewSettings(e.Args)) { Shutdown(); return; }
        if (TryPreviewSetup(e.Args)) { Shutdown(); return; }

        // A portable app that can only be started from a .cmd does not read as
        // a real program. The shortcut is written by the shell itself, so it is
        // an ordinary .lnk that Explorer, the Start menu and "pin to taskbar"
        // all accept - a hand-built file is rejected by half of them.
        EnsureDesktopShortcut();
        EnsurePortableShortcut();

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
        // `DropLink.exe --settings` is what the portable kit's settings shortcut
        // runs. A second launch with it must reach the instance that already owns
        // the receiver, so the request rides its own named event.
        var openSettings = e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase);
        singleInstance = new SingleInstance(
            () => _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(ShowExistingWindow)),
            () => _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(ShowWindowAndSettings)),
            requestOpenSettings: openSettings);
        if (!singleInstance.IsFirst)
        {
            // The one case that must never be silent: something is holding the
            // lock and would not show a window. Exiting quietly here is what made
            // the app look like it refused to open at all.
            if (singleInstance.UnresponsiveOwnerPid > 0)
            {
                MessageBox.Show(
                    "DropLink 已经在运行，但它没有响应，也没能自动结束。\n\n" +
                    "请打开「任务管理器」→「详细信息」→ 结束 DropLink.exe（PID " +
                    singleInstance.UnresponsiveOwnerPid + "），然后重新打开。",
                    "DropLink", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }
        StartStartupWatchdog();
        // We are the only receiver in this session, so any backend still running
        // is a leftover from a run that never cleaned up. It would hold the radio
        // and the stop event and make every start fail.
        //
        // Off the UI thread: this walks the process list and waits on kills, and
        // a machine with several orphans in a bad state can make it take minutes.
        // The window must not wait for it - a receiver that opens is worth more
        // than a receiver that tidied up first.
        ownsChain = true;
        _ = Task.Run(() =>
        {
            try
            {
                var killed = BackendChain.KillStaleChain();
                if (killed > 0) AppLog.Write($"cleaned {killed} stale backend process(es) from a previous run");
            }
            catch (Exception error) { AppLog.Write("stale chain cleanup failed: " + error.Message); }
        });

        // Last-resort crash guard: never die silently. async void handlers in
        // WPF can throw outside try/catch; show the reason instead of vanishing.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.WriteException("dispatcher", args.Exception);
            args.Handled = true;
            // Nothing is worth a modal box on the way out: the user asked the app
            // to close, and a dialog they did not ask for now holds the process
            // open and makes "关闭" mean something else. Logged above instead.
            if (ShuttingDown) return;
            MessageBox.Show(Describe(args.Exception),
                "DropLink 异常", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) AppLog.WriteException("appdomain", error);
            if (ShuttingDown) return;
            MessageBox.Show(
                "DropLink 遇到严重问题：\n" +
                (args.ExceptionObject as Exception)?.Message +
                "\n\n（详细信息已写入 data\\logs\\app.log）",
                "DropLink 严重异常", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        // The lock is only worth holding while a window exists to go with it.
        windowReady = true;
        if (StartMinimized)
        {
            // The window is never shown, so Loaded never fires: kick the guide
            // and the receiver off explicitly or a tray-only launch does nothing.
            _ = window.InitializeAsync();
        }
        else
        {
            window.Show();
            if (openSettings) window.OpenSettingsWindow();
        }
        singleInstance?.ConfirmAlive();
    }

    // Set once the user has asked the app to close. While it is set, an
    // unhandled exception is logged and swallowed rather than shown: a dialog
    // during shutdown keeps a process the user has already dismissed alive.
    internal static bool ShuttingDown { get; set; }

    // An exception with no message produced a dialog with an empty line and a
    // bare type name, which reads as "something broke and nobody knows what".
    // Say what is actually known, and where the stack went.
    private static string Describe(Exception error)
    {
        var text = string.IsNullOrWhiteSpace(error.Message)
            ? "（这个错误没有附带说明：" + error.GetType().Name + "）"
            : error.Message;
        var inner = error.InnerException;
        var detail = error.GetType().Name + (inner is null ? "" : " / " + inner.GetType().Name);
        return "DropLink 遇到问题：\n" + text +
               "\n\n（详细信息：" + detail + "，已写入 data\\logs\\app.log）";
    }

    // A launch that never manages to build a window must not keep the
    // single-instance lock: it would answer every later double-click with
    // silence, and only Task Manager could clear it. Releasing the lock by
    // exiting is the difference between "it did not start" and "it is now
    // permanently broken".
    private void StartStartupWatchdog()
    {
        var thread = new Thread(() =>
        {
            for (var tick = 0; tick < 60 && !windowReady; tick++) Thread.Sleep(500);
            if (windowReady) return;
            AppLog.Write("startup watchdog: no window after 30 s, releasing the lock and exiting");
            Environment.Exit(4);
        })
        {
            IsBackground = true,
            Name = "DropLink startup watchdog"
        };
        thread.Start();
    }

    // `DropLink.exe --preview-main out.png [state]` renders one main-window
    // state and exits. The window's job is to say exactly one true thing, and
    // that cannot be reviewed from source - the same reason the settings page
    // has a preview switch.
    private static bool TryPreviewMain(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--preview-main", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        if (index + 1 >= args.Length) return true;
        var target = args[index + 1];
        var state = index + 2 < args.Length && !args[index + 2].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 2] : "waiting";
        try
        {
            var window = new MainWindow(previewOnly: true) { ShowInTaskbar = false };
            window.Show();
            window.PreviewState(state);
            window.UpdateLayout();
            var root = (FrameworkElement)window.Content;
            root.UpdateLayout();
            var width = (int)Math.Ceiling(root.ActualWidth);
            var height = (int)Math.Ceiling(root.ActualHeight);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(width, 1), Math.Max(height, 1), 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            using (var stream = File.Create(target)) encoder.Save(stream);
            AppLog.Write($"preview-main wrote {target} ({width}x{height}, state={state})");
        }
        catch (Exception error)
        {
            AppLog.Write("preview-main failed: " + error.Message);
        }
        return true;
    }

    // `DropLink.exe --preview-setup out.png 2` renders one screen of the first-run
    // flow. The facts on it are probed for real, so the PNG cannot show a state
    // the window would never reach.
    private static bool TryPreviewSetup(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--preview-setup", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        if (index + 1 >= args.Length) return true;
        var target = args[index + 1];
        var step = index + 2 < args.Length && int.TryParse(args[index + 2], out var parsed) ? parsed : 1;
        try
        {
            var window = new SetupWindow(() => Task.FromResult((false, "预览"))) { ShowInTaskbar = false };
            window.Show();
            var dispatcher = Current.Dispatcher;
            var frame = new DispatcherFrame();
            var giveUp = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Normal,
                (_, _) => frame.Continue = false, dispatcher);
            giveUp.Start();
            _ = window.PreviewAsync(step).ContinueWith(_ => dispatcher.BeginInvoke(
                new Action(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame);
            giveUp.Stop();
            // The window resizes once when the probe fills the body in; measuring
            // before that yields a bitmap that cuts the button row off.
            dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var root = (FrameworkElement)window.Content;
            root.UpdateLayout();
            var width = (int)Math.Ceiling(root.ActualWidth);
            var height = (int)Math.Ceiling(root.ActualHeight);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(width, 1), Math.Max(height, 1), 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            using (var stream = File.Create(target)) encoder.Save(stream);
            AppLog.Write($"preview-setup wrote {target} ({width}x{height}, step={step})");
        }
        catch (Exception error)
        {
            AppLog.Write("preview-setup failed: " + error.Message);
        }
        return true;
    }

    // `DropLink.exe --preview-settings out.png` renders the settings page exactly
    // as it is laid out and writes a PNG. It exists because the page's whole job
    // is to not look crowded, and that cannot be reviewed from source.
    private static bool TryPreviewSettings(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--preview-settings", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        if (index + 1 >= args.Length) return true;
        var target = args[index + 1];
        try
        {
            var page = new SettingsWindow(null) { ShowInTaskbar = false };
            page.Show();
            // The page fills in the autostart switch from a PowerShell query; wait
            // for that answer, otherwise the screenshot shows a half-loaded page.
            // The dispatcher has to be the UI one captured here: inside the
            // continuation Dispatcher.CurrentDispatcher would create a fresh one
            // on the pool thread and the frame would only end on the timeout.
            var dispatcher = Current.Dispatcher;
            var frame = new DispatcherFrame();
            var giveUp = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Normal,
                (_, _) => frame.Continue = false, dispatcher);
            giveUp.Start();
            _ = page.Ready.ContinueWith(_ => dispatcher.BeginInvoke(
                new Action(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame);
            giveUp.Stop();

            // Render the content root, not the Window: Window.ActualHeight
            // includes the title bar, which would leave a transparent band at the
            // bottom of the PNG and make the page look taller than it is.
            var root = (FrameworkElement)page.Content;
            root.UpdateLayout();
            var width = (int)Math.Ceiling(root.ActualWidth);
            var height = (int)Math.Ceiling(root.ActualHeight);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                Math.Max(width, 1), Math.Max(height, 1), 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            using (var stream = File.Create(target)) encoder.Save(stream);
            AppLog.Write($"preview-settings wrote {target} ({width}x{height})");
        }
        catch (Exception error)
        {
            AppLog.Write("preview-settings failed: " + error.Message);
        }
        return true;
    }

    // Written once, and never overwritten while it still points at this exe: a
    // shortcut the user renamed or moved is theirs, and silently recreating it
    // every launch would be its own kind of rude. Failure is logged and
    // ignored - the receiver still has to work.
    //
    // "Still points at this exe" is tracked in data\shortcut.target rather than
    // read back out of the .lnk. A shortcut left behind by a moved package is a
    // button that does nothing, and the app that could repair it is exactly the
    // thing the dead shortcut was supposed to start.
    private static void EnsureDesktopShortcut()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop)) return;
            var link = Path.Combine(desktop, "DropLink.lnk");
            var stamp = Path.Combine(AppPaths.DataDirectory(), "shortcut.target");
            if (File.Exists(link) && File.Exists(stamp) &&
                string.Equals(File.ReadAllText(stamp).Trim(), exe, StringComparison.OrdinalIgnoreCase))
                return;
            if (!CreateShortcut(link, exe)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
            File.WriteAllText(stamp, exe);
        }
        catch (Exception error)
        {
            AppLog.Write("desktop shortcut failed: " + error.Message);
        }
    }

    // The package root is the only place most people look, so the button they
    // need belongs there instead of one folder down. Same shell call as the
    // desktop shortcut on purpose: the shell rejects a hand-built .lnk, which is
    // how a "start here" file ends up doing nothing when double-clicked.
    private static void EnsurePortableShortcut()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var link = Path.Combine(AppPaths.PortableRoot(), "启动 DropLink.lnk");
            if (File.Exists(link)) return;
            CreateShortcut(link, exe);
        }
        catch (Exception error)
        {
            AppLog.Write("portable shortcut failed: " + error.Message);
        }
    }

    private static bool CreateShortcut(string link, string exe)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.IconLocation = exe + ",0";
            shortcut.Description = "DropLink - iPhone 隔空投送接收器";
            shortcut.Save();
            AppLog.Write("shortcut written: " + link);
            return true;
        }
        catch (Exception error)
        {
            AppLog.Write("shortcut failed for " + link + ": " + error.Message);
            return false;
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
            // Tell the launch that woke us that a window really is on screen.
            // Until this line runs, the other process is entitled to assume we
            // are wedged and take the lock away.
            singleInstance?.ConfirmAlive();
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
                singleInstance?.ConfirmAlive();
                return;
            }
            AppLog.Write("activate requested but no window exists yet");
        });
    }

    private void ShowWindowAndSettings()
    {
        ShowExistingWindow();
        if (MainWindow is MainWindow ready) ready.OpenSettingsWindow();
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
