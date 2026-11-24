using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace DropLink;

/// <summary>
/// Keeps DropLink alive in the notification area. Closing or minimizing the
/// window hides it instead of ending the receiver; only the tray menu exits.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Window window;
    private readonly Forms.NotifyIcon icon;
    private bool balloonShown;

    public TrayIcon(Window window, Action exit)
    {
        this.window = window;
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        var open = new Forms.ToolStripMenuItem("打开主界面");
        open.Click += (_, _) => ShowWindow();
        var settings = new Forms.ToolStripMenuItem("设置");
        settings.Click += (_, _) => OpenSettings();
        var folder = new Forms.ToolStripMenuItem("打开接收文件夹");
        folder.Click += (_, _) => OpenReceiveFolder();
        var diagnostics = new Forms.ToolStripMenuItem("导出诊断包");
        diagnostics.Click += (_, _) => ExportDiagnostics();
        var quit = new Forms.ToolStripMenuItem("退出 DropLink");
        quit.Click += (_, _) => exit();
        menu.Items.Add(open);
        menu.Items.Add(settings);
        menu.Items.Add(folder);
        menu.Items.Add(diagnostics);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quit);

        icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DropLink - 隔空投送接收器",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => ShowWindow();
    }

    public void ShowWindow()
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    public void HideWindow()
    {
        window.Hide();
        if (balloonShown) return;
        balloonShown = true;
        try
        {
            icon.ShowBalloonTip(3000, "DropLink 还在后台",
                "窗口已收到托盘。双击托盘图标可以重新打开。", Forms.ToolTipIcon.Info);
        }
        catch (Exception) { /* balloons are best-effort */ }
    }

    public void SetReceiving(bool on) =>
        icon.Text = on ? "DropLink - 正在接收" : "DropLink - 隔空投送接收器";

    /// <summary>A background receiver reports problems here instead of a modal box.</summary>
    public void Notify(string title, string text)
    {
        try { icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Warning); }
        catch (Exception) { /* balloons are best-effort */ }
    }

    private static Icon LoadIcon()
    {
        // Prefer the embedded multi-size icon so the tray gets a crisp 16x16.
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("app.ico", UriKind.Relative));
            if (res is not null)
            {
                using var stream = res.Stream;
                return new Icon(stream, new System.Drawing.Size(16, 16));
            }
        }
        catch (Exception) { /* fall through to the executable icon */ }
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
                return Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application;
        }
        catch (Exception) { /* fall back to the stock icon */ }
        return SystemIcons.Application;
    }

    private static void OpenSettings()
    {
        var script = Path.Combine(PortableRoot(), "backend", "airdrop", "settings-gui.ps1");
        if (!File.Exists(script))
        {
            MessageBox.Show("找不到设置脚本：\n" + script, "DropLink",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(script) ?? PortableRoot(),
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\""
        };
        Process.Start(info);
    }

    private static void OpenReceiveFolder()
    {
        var folder = Environment.GetEnvironmentVariable("DROPLINK_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(folder))
            folder = Path.Combine(PortableRoot(), "data", "received");
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"")
        {
            UseShellExecute = true
        });
    }

    // Runs the settings tool off the UI thread: it zips logs plus a doctor
    // snapshot, then tells the user where it landed and opens the folder.
    private void ExportDiagnostics()
    {
        var script = Path.Combine(PortableRoot(), "backend", "airdrop", "settings-tool.ps1");
        _ = Task.Run(() =>
        {
            string message;
            var ok = false;
            try
            {
                if (!File.Exists(script))
                {
                    message = "找不到诊断脚本：" + script;
                }
                else
                {
                    var info = new ProcessStartInfo("powershell.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(script) ?? PortableRoot(),
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Action export-diagnostics"
                    };
                    using var process = Process.Start(info);
                    if (process is null)
                    {
                        message = "无法启动诊断脚本。";
                    }
                    else
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        _ = process.StandardError.ReadToEnd();
                        process.WaitForExit(60000);
                        var line = output.Split('\n')
                            .FirstOrDefault(l => l.TrimStart().StartsWith("OK ", StringComparison.Ordinal));
                        if (line is null) message = "导出失败，详见 data\\logs\\app.log。";
                        else { message = "已导出：" + line.Trim()[3..]; ok = true; }
                    }
                }
            }
            catch (Exception error) { message = "导出失败：" + error.Message; }

            window.Dispatcher.BeginInvoke(() =>
            {
                Notify("DropLink 诊断包", message);
                if (ok) OpenReceiveFolderFor(Path.Combine(PortableRoot(), "data"));
            });
        });
    }

    private static void OpenReceiveFolderFor(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception) { /* opening a folder is best-effort */ }
    }

    private static string PortableRoot()
    {
        var exe = Environment.ProcessPath;
        var appDir = string.IsNullOrWhiteSpace(exe)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        return Directory.GetParent(appDir)?.FullName ?? appDir;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
