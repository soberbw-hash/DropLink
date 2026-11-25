using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;

namespace DropLink;

/// <summary>
/// Settings live in the app instead of a PowerShell form. The old panel placed
/// every control by absolute Left/Top in WinForms, so it opened a second window
/// with a console host behind it and read as a wall of widgets; this one is a
/// normal WPF page that writes the same data\settings.json the engine reads.
///
/// Every control applies the moment it changes. A save button would mean the
/// page can show one thing while the file says another, and "did that stick"
/// is exactly the kind of doubt this app is trying to get rid of.
/// </summary>
public partial class SettingsWindow : Window
{
    // The engine rejects names containing these (receiver.valid_name ->
    // transfer.UNSAFE_CHAR): separators, what Windows forbids, control
    // characters and bidi overrides. Checking here means the page never claims
    // a rename succeeded when the receiver refused it.
    private static readonly Regex UnsafeName = new(
        "[/\\\\\\x00<>:\"|?*]|[\\u202a-\\u202e\\u2066-\\u2069\\u200b-\\u200f\\ufeff]|[\\x01-\\x1f\\x7f-\\x9f]",
        RegexOptions.Compiled);

    private readonly Func<string, Task<bool>>? applyDeviceName;
    private readonly Func<string, Task<bool>>? applyReceiveFolder;
    private readonly Action? clearHistory;
    /// <summary>Screenshot mode. The page is committed to the repository as a
    /// PNG, so it must show sample values instead of this machine's folder and
    /// host name.</summary>
    private readonly bool preview;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string hostName = System.Net.Dns.GetHostName().Split('.')[0];
    private JsonObject settings = new();
    private string committedName = "";
    private bool autostartState;
    private bool loading = true;
    private bool applyingAutostart;

    private const string SampleFolder = @"C:\Users\you\Downloads\DropLink";
    private const string SampleName = "我的电脑";

    internal SettingsWindow(Func<string, Task<bool>>? applyDeviceName,
        Func<string, Task<bool>>? applyReceiveFolder = null, Action? clearHistory = null,
        bool preview = false)
    {
        InitializeComponent();
        this.applyDeviceName = applyDeviceName;
        this.applyReceiveFolder = applyReceiveFolder;
        this.clearHistory = clearHistory;
        this.preview = preview;
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>Completes once the page has read settings and the autostart state.</summary>
    internal Task Ready => ready.Task;

    private string SettingsPath => Path.Combine(AppPaths.DataDirectory(), "settings.json");

    private static string ToolScript =>
        Path.Combine(AppPaths.PortableRoot(), "backend", "airdrop", "settings-tool.ps1");

    private async Task LoadAsync()
    {
        try { await LoadCoreAsync(); }
        finally
        {
            loading = false;
            ready.TrySetResult();
        }
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            settings = ReadSettings();
            committedName = Str("device_name");
            DeviceNameBox.Text = preview ? "" : committedName;
            // Same source the engine uses for its default (socket.gethostname),
            // so the placeholder is the name the phone will really show.
            DeviceNamePlaceholder.Text = preview ? SampleName : hostName;
            DeviceNameBox.TextChanged += (_, _) => UpdateNamePlaceholder();
            UpdateNamePlaceholder();
            AutoReceiveSwitch.IsChecked = Bool("auto_receive", true);
            LivePhotoSwitch.IsChecked = Bool("live_photo_still_copy", true);
            FormatSwitch.IsChecked = Bool("show_format_in_notification", true);
            FolderText.Text = preview ? SampleFolder : SettingsFile.ReceiveFolder();
            VersionText.Text = Version();
            HistoryHint.Text = "首页显示最近三次。清空列表不会删除已经收到的文件。";
        }
        catch (Exception error)
        {
            SetNote("读取设置失败：" + error.Message, true);
        }

        // Reading the scheduled task takes a second or two of PowerShell; the
        // page is usable immediately and the switch fills in when it answers.
        AutostartSwitch.IsEnabled = false;
        var state = await Task.Run(() => RunTool("autostart-state"));
        AutostartSwitch.IsEnabled = true;
        if (state.ExitCode == 0)
        {
            autostartState = state.Output.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
            AutostartSwitch.IsChecked = autostartState;
            AutostartHint.Text = autostartState
                ? "已开启。登录 Windows 后会在后台直接开始接收，不弹窗口、不弹 UAC。"
                : "登录 Windows 后在后台开始接收，不弹窗口、不弹 UAC。";
        }
        else
        {
            // Say the state is unknown rather than showing a switch that may be
            // the opposite of the truth. The reason goes to the status line:
            // a script path inside a row hint runs under the switch.
            AutostartSwitch.IsEnabled = false;
            AutostartHint.Text = "读不到开机启动状态，这个开关暂时不可用。";
            SetNote("开机启动状态读取失败：" + state.Output.Trim(), true);
        }
    }

    private void UpdateNamePlaceholder() =>
        DeviceNamePlaceholder.Visibility =
            DeviceNameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private string Str(string key) =>
        settings[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private bool Bool(string key, bool fallback) =>
        settings[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private JsonObject ReadSettings() => SettingsFile.Read();

    private void WriteSettings() => SettingsFile.Save(settings);

    /// <summary>
    /// The three receive switches. They are startup options - the engine reads
    /// them when the receiver starts, and auto-receive cannot be flipped on a
    /// running proxy-mode engine - so the note says when they take effect
    /// instead of implying the change is already live.
    /// </summary>
    private void ReceiveSetting_Toggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        try
        {
            settings["auto_receive"] = AutoReceiveSwitch.IsChecked == true;
            settings["live_photo_still_copy"] = LivePhotoSwitch.IsChecked == true;
            settings["show_format_in_notification"] = FormatSwitch.IsChecked == true;
            WriteSettings();
            SetNote("已保存。这几项在下一次开始接收时生效。");
        }
        catch (Exception error)
        {
            SetNote("保存失败：" + error.Message, true);
        }
    }

    private async void DeviceName_Commit(object sender, RoutedEventArgs e) => await CommitNameAsync();

    private async void DeviceName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await CommitNameAsync();
    }

    private async Task CommitNameAsync()
    {
        if (loading) return;
        var typed = DeviceNameBox.Text.Trim();
        if (typed == committedName) return;
        if (typed.Length > 0 && UnsafeName.IsMatch(typed))
        {
            SetNote("这个名字用不了：不能包含 / \\ : * ? \" < > | 或看不见的控制字符。", true);
            return;
        }

        var previous = settings["device_name"]?.ToString() ?? "";
        try
        {
            settings["device_name"] = typed;
            WriteSettings();
        }
        catch (Exception error)
        {
            settings["device_name"] = previous;
            SetNote("保存失败：" + error.Message, true);
            return;
        }
        committedName = typed;

        // An empty box means "use the computer name"; push that resolved name so
        // the phone shows what the placeholder promised.
        var effective = typed.Length > 0 ? typed : hostName;
        if (applyDeviceName is null)
        {
            SetNote("已保存。");
            return;
        }
        try
        {
            // false means the receiver is not running: saved, but not live yet.
            var live = await applyDeviceName(effective);
            SetNote(live
                ? "已改名为「" + effective + "」，iPhone 的隔空投送列表里马上就能看到。"
                : "已保存。下次开始接收时用「" + effective + "」。");
        }
        catch (Exception error)
        {
            // The file is already correct; only the live rename failed. Say which
            // one happened instead of claiming the change is live.
            SetNote("已保存。下次开始接收时用「" + effective + "」（" + error.Message + "）。", true);
        }
    }

    private async void Autostart_Toggled(object sender, RoutedEventArgs e)
    {
        if (loading || applyingAutostart) return;
        var want = AutostartSwitch.IsChecked == true;
        applyingAutostart = true;
        AutostartSwitch.IsEnabled = false;
        SetNote(want ? "正在开启开机自动启动…" : "正在关闭开机自动启动…");
        try
        {
            var result = await Task.Run(() => RunTool(want ? "autostart-on" : "autostart-off"));
            if (result.ExitCode == 0)
            {
                autostartState = want;
                AutostartHint.Text = want
                    ? "已开启。登录 Windows 后会在后台直接开始接收，不弹窗口、不弹 UAC。"
                    : "登录 Windows 后在后台开始接收，不弹窗口、不弹 UAC。";
                SetNote(want ? "已开启开机自动启动。" : "已关闭开机自动启动。");
            }
            else
            {
                // Put the switch back. A tick that did nothing is the worst
                // possible answer to "我打上勾就会开机自启".
                loading = true;
                AutostartSwitch.IsChecked = autostartState;
                loading = false;
                SetNote((want ? "开启" : "关闭") + "开机自动启动失败：" + result.Output.Trim(), true);
            }
        }
        finally
        {
            applyingAutostart = false;
            AutostartSwitch.IsEnabled = true;
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    private static string Version()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var shown = info.ProductVersion ?? info.FileVersion;
                // SourceLink appends the whole commit hash to ProductVersion.
                // A 40-character hash is not a version number to a user.
                if (!string.IsNullOrWhiteSpace(shown))
                {
                    var cut = shown.IndexOf('+');
                    if (cut > 0) shown = shown[..cut];
                    return "版本 " + shown.Trim();
                }
            }
        }
        catch (Exception) { /* fall through to the assembly version */ }
        return "版本 " + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    }

    /// <summary>
    /// The folder picker writes the choice, then asks the receiver to move to
    /// it. Files already received stay where they are - moving a user's files
    /// because a setting changed would be the app overstepping.
    /// </summary>
    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择接收文件的保存位置",
            InitialDirectory = SettingsFile.ReceiveFolder(),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var chosen = dialog.FolderName;
        try
        {
            settings["receive_folder"] = chosen;
            WriteSettings();
            FolderText.Text = chosen;
        }
        catch (Exception error)
        {
            SetNote("保存失败：" + error.Message, true);
            return;
        }
        if (applyReceiveFolder is null) { SetNote("已保存，下一次开始接收时使用这个位置。"); return; }
        try
        {
            var live = await applyReceiveFolder(chosen);
            SetNote(live
                ? "已改到：" + chosen
                : "已保存。下一次开始接收时使用这个位置（正在接收的这批会存到原位置）。");
        }
        catch (Exception error)
        {
            SetNote("已保存。重启接收后生效（" + error.Message + "）。", true);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "清空接收历史列表？\n\n已经收到的文件不会被删除。", "清空接收历史",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            clearHistory?.Invoke();
            SetNote("列表已清空。文件还在原来的文件夹里。");
        }
        catch (Exception error) { SetNote("清空失败：" + error.Message, true); }
    }

    // Driver rollback lives here now instead of on the main window: it is a
    // maintenance action, not something the daily screen should offer.
    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var script = Environment.GetEnvironmentVariable("DROPLINK_ROLLBACK_CMD") ?? "";
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
        {
            ShowResult("找不到回退脚本：" + script);
            return;
        }
        if (MessageBox.Show(this, "把网卡恢复成原厂驱动？\n\n恢复后这张网卡可以继续当 Wi-Fi 用，\n但 DropLink 会停止接收，直到重新配置。\n\n继续？",
                "回退驱动", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        ShowResult("正在回退，最多 1 分钟…");
        try
        {
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(script)!
            };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Force" })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动脚本。");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            await process.WaitForExitAsync();
            ShowResult(process.ExitCode == 0
                ? "已恢复成原厂驱动。"
                : "回退没有成功（退出码 " + process.ExitCode + "）：" + (output + error).Trim());
        }
        catch (Exception ex) { ShowResult("回退失败：" + ex.Message); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Environment.GetEnvironmentVariable("DROPLINK_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(folder)) folder = Path.Combine(AppPaths.DataDirectory(), "received");
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception error) { ShowResult("打开文件夹失败：" + error.Message); }
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        ShowResult("正在导出诊断包…");
        var result = await Task.Run(() => RunTool("export-diagnostics"));
        var line = result.Output.Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("OK ", StringComparison.Ordinal));
        ShowResult(result.ExitCode == 0 && line is not null
            ? "已导出：" + line.Trim()[3..]
            : "导出失败：" + result.Output.Trim());
    }

    private async void CheckDriver_Click(object sender, RoutedEventArgs e)
    {
        ShowResult("正在检查…");
        var result = await Task.Run(() => RunTool("checkdriver"));
        ShowResult(result.Output.Trim() is { Length: > 0 } text ? text : "没有返回结果。");
    }

    private void ShowResult(string message) => MaintenanceResult.Text = message;

    private void SetNote(string message, bool bad = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(bad
            ? System.Windows.Media.Color.FromRgb(0xB2, 0x3A, 0x48)
            : System.Windows.Media.Color.FromRgb(0x8B, 0x97, 0xAC));
    }

    private readonly record struct ToolResult(int ExitCode, string Output);

    // Hidden by design: the whole point of this page is that the user never
    // sees a console window. The scripts stay as the implementation because
    // they already own the task registration and the PnP queries.
    private static ToolResult RunTool(string action)
    {
        try
        {
            if (!File.Exists(ToolScript)) return new ToolResult(1, "找不到设置脚本：" + ToolScript);
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ToolScript)!
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ToolScript, "-Action", action })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动设置脚本。");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(60000);
            var text = (output + (string.IsNullOrWhiteSpace(error) ? "" : "\n" + error)).Trim();
            return new ToolResult(process.ExitCode, text);
        }
        catch (Exception error)
        {
            return new ToolResult(1, error.Message);
        }
    }
}
