using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace DropLink;

/// <summary>
/// A shortcut and a status readout next to the clock. It no longer hides the
/// window: closing the window quits DropLink, and minimizing leaves it in the
/// taskbar where a normal program belongs.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Window window;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem statusItem;
    private readonly Forms.ToolStripMenuItem pauseItem;

    public TrayIcon(Window window, Action exit, Action openSettings, Action togglePause, Func<bool> isPaused)
    {
        this.window = window;
        // The first item restates the header, so a right-click answers "还在收吗"
        // without opening anything. It is disabled: it is a readout, not a verb.
        statusItem = new Forms.ToolStripMenuItem("状态：正在启动") { Enabled = false };
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        var open = new Forms.ToolStripMenuItem("打开 DropLink");
        open.Click += (_, _) => ShowWindow();
        pauseItem = new Forms.ToolStripMenuItem("暂停接收");
        pauseItem.Click += (_, _) => togglePause();
        var settings = new Forms.ToolStripMenuItem("设置");
        // Opens the in-app page. It used to launch settings-gui.ps1, which put a
        // PowerShell console on screen and a second, differently styled form on
        // top of it - the user read that as "why does settings jump to PowerShell".
        settings.Click += (_, _) => openSettings();
        var folder = new Forms.ToolStripMenuItem("打开接收文件夹");
        folder.Click += (_, _) => OpenReceiveFolder();
        var diagnostics = new Forms.ToolStripMenuItem("导出诊断包");
        diagnostics.Click += (_, _) => ExportDiagnostics();
        var quit = new Forms.ToolStripMenuItem("退出 DropLink");
        quit.Click += (_, _) => exit();
        menu.Items.Add(statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(open);
        menu.Items.Add(pauseItem);
        menu.Items.Add(folder);
        menu.Items.Add(settings);
        menu.Items.Add(diagnostics);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quit);

        icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DropLink · 正在启动",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => ShowWindow();
        SetPaused(isPaused());
    }

    /// <summary>The menu item says what the next click does, not what the state is.</summary>
    public void SetPaused(bool paused)
    {
        try { pauseItem.Text = paused ? "恢复接收" : "暂停接收"; }
        catch (Exception) { /* keep the previous text */ }
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

    /// <summary>
    /// Mirrors the header line. NotifyIcon.Text is capped by the shell and going
    /// over throws rather than truncating, so the text is shortened here - a
    /// tray tooltip that silently stops updating would be its own small lie.
    /// </summary>
    public void SetStatus(string status)
    {
        const string prefix = "DropLink · ";
        var room = 63 - prefix.Length;
        var tip = status.Length <= room ? status : status[..(room - 1)] + "…";
        try { icon.Text = prefix + tip; }
        catch (Exception) { /* keep the previous tooltip */ }
        try { statusItem.Text = "状态：" + status; }
        catch (Exception) { /* keep the previous menu text */ }
    }

    /// <summary>A background receiver reports problems here instead of a modal box.</summary>
    public void Notify(string title, string text)
    {
        try { icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Warning); }
        catch (Exception) { /* balloons are best-effort */ }
    }

    /// <summary>
    /// The tray slot in physical pixels. WinForms knows the answer, but its DPI
    /// context is not always initialised in a WPF process and a stale 16 there
    /// is exactly the bug being fixed - so the display scale is also asked
    /// directly and the larger of the two wins.
    /// </summary>
    private static System.Drawing.Size TrayIconSize()
    {
        var slot = Forms.SystemInformation.SmallIconSize;
        try
        {
            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            var scaled = (int)Math.Round(16 * graphics.DpiX / 96.0);
            if (scaled > slot.Width) slot = new System.Drawing.Size(scaled, scaled);
        }
        catch (Exception) { /* keep whatever WinForms said */ }
        if (slot.Width < 16 || slot.Height < 16) slot = new System.Drawing.Size(16, 16);
        return slot;
    }

    private static Icon LoadIcon()
    {
        // The embedded multi-size icon is what gives the tray a crisp icon.
        // It has to come from the manifest resource: a .ico declared as a WPF
        // Resource is dropped by the resources generator, which is why this
        // branch used to fail silently and fall through to the executable icon.
        //
        // The size is not fixed at 16. The tray slot is measured in physical
        // pixels and follows the display scale (16 at 100%, 24 at 150%, 32 at
        // 200%), so asking for 16 made the shell draw a 16px bitmap inside a
        // 24px slot: the icon sat visibly smaller than the apps next to it.
        var slot = TrayIconSize();
        AppLog.Write($"tray icon requested at {slot.Width}x{slot.Height}");
        try
        {
            using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("app.ico");
            if (stream is not null) return new Icon(stream, slot);
        }
        catch (Exception) { /* fall through */ }
        // Published builds also ship app\DropLink.ico next to the exe.
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var shipped = Path.Combine(Path.GetDirectoryName(path)!, "DropLink.ico");
                if (File.Exists(shipped)) return new Icon(shipped, slot);
                return Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application;
            }
        }
        catch (Exception) { /* fall back to the stock icon */ }
        return SystemIcons.Application;
    }

    private static void OpenReceiveFolder()
    {
        var folder = SettingsFile.ReceiveFolder();
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

    private static string PortableRoot() => AppPaths.PortableRoot();

    public void Dispose()
    {
        // Two calls, two chances to throw, and this runs on the way out where an
        // escaping exception is most expensive. WinForms has been seen to raise
        // FileNotFoundException from inside NotifyIcon teardown; whatever it is,
        // it belongs in the log, not in a dialog over a closing window.
        try { icon.Visible = false; }
        catch (Exception error) { AppLog.WriteException("tray: hide", error); }
        try { icon.Dispose(); }
        catch (Exception error) { AppLog.WriteException("tray: dispose", error); }
    }
}
