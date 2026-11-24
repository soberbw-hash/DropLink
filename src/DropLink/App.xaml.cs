using System.Windows;
using System.Windows.Threading;

namespace DropLink;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Self-contained: derive backend/driver paths even if launched by
        // double-clicking the exe with no launcher environment at all.
        AppPaths.EnsureEnvironment();

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
    }
}
