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
        var quit = new Forms.ToolStripMenuItem("退出 DropLink");
        quit.Click += (_, _) => exit();
        menu.Items.Add(open);
        menu.Items.Add(settings);
        menu.Items.Add(folder);
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
