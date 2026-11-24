using System.IO;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace DropLink;
public partial class App : System.Windows.Application
{
    private SingleInstance? singleInstance;

    internal static bool StartMinimized { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartMinimized = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        // Self-contained: derive backend/driver paths even if launched by
        // double-clicking the exe with no launcher environment at all.
        AppPaths.EnsureEnvironment();

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
        singleInstance = new SingleInstance(() =>
            _ = Dispatcher.InvokeAsync(
                () => (MainWindow as MainWindow)?.ShowFromTray(),
                DispatcherPriority.ApplicationIdle));
        if (!singleInstance.IsFirst)
        {
            Shutdown();
            return;
        }

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

        var window = new MainWindow { BackgroundMode = StartMinimized };
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

    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
