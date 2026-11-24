using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace DropLink;
public partial class MainWindow : Window
{
    private readonly TransferState transferState = new();
    private ReceiverSession? session;
    private GuideStep currentStep = GuideStep.EnvironmentBlocked;
    private bool detailsVisible;
    public MainWindow()
    {
        InitializeComponent();
        Transfers.ItemsSource = transferState.Items;
        // Auto-check on launch so the guide is useful the moment the window opens.
        Loaded += async (_, _) => { await ScanAsync(); await CheckReadinessAsync(); };
        Closed += async (_, _) => await CloseSessionAsync();
    }

    // A future validated chain coordinator calls this; nothing starts at launch.
    // The receive button stays disabled until device selection and recovery are proven.
    private async Task StartSessionAsync(
        ChainFiles files, IReadOnlyList<RadioDevice> devices, string selectedInstance,
        bool authorized, bool recoveryVerified,
        string python, string engineScript, string outputDirectory, string keysDirectory, string deviceName,
        UsbDeviceSnapshot? selectedTarget = null)
    {
        if (session is not null) throw new InvalidOperationException("接收会话已经启动。");
        var created = new ReceiverSession();
        created.EngineEvent += OnEngineEvent;
        created.Failed += OnSessionFailed;
        session = created;
        try
        {
            await created.StartAsync(files, devices, selectedInstance, authorized, recoveryVerified,
                python, engineScript, outputDirectory, keysDirectory, deviceName, selectedTarget);
        }
        catch
        {
            await created.DisposeAsync();
            session = null;
            throw;
        }
    }

    private async Task CloseSessionAsync()
    {
        var current = session;
        session = null;
        if (current is not null) await current.DisposeAsync();
    }

    private void OnSessionFailed(string message) =>
        _ = Dispatcher.BeginInvoke(() => MessageBox.Show(this, message, "接收链路异常", MessageBoxButton.OK, MessageBoxImage.Warning));

    // Engine facts are applied on the UI thread; an 'asked' event triggers an
    // explicit confirmation whose answer is sent back through the session.
    private void OnEngineEvent(JsonElement message)
    {
        _ = Dispatcher.BeginInvoke(async () =>
        {
            var row = transferState.Apply(message);
            if (row is null || !message.TryGetProperty("event", out var kind) || kind.GetString() != "asked") return;
            var answer = MessageBox.Show(this, $"{row.Sender} 想发送：\n{row.Files}\n\n是否接收？",
                "接收请求", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            try { await (session?.AnswerAsync(row.Id, answer == MessageBoxResult.Yes) ?? Task.CompletedTask); }
            catch (Exception) { row.Status = "无法发送答复"; }
        });
    }

    // Read-only auto-check: drives the guided card. Never opens a device,
    // changes a driver or starts any backend process.
    private async Task CheckReadinessAsync()
    {
        GuideTitle.Text = "正在检查设备与驱动…";
        try
        {
            var devices = await QueryRadioDevicesAsync();
            var files = new[] { "DROPLINK_RADIO_EXE", "DROPLINK_BRIDGE_EXE", "DROPLINK_NETD_EXE", "DROPLINK_AWDLD_EXE" }
                .Select(k => Environment.GetEnvironmentVariable(k) ?? "").ToArray();
            bool authorized = EnvFlag("DROPLINK_AUTHORIZED");
            bool recovery = EnvFlag("DROPLINK_RECOVERY_VERIFIED");
            var radioExe = Environment.GetEnvironmentVariable("DROPLINK_RADIO_EXE") ?? "";
            var inventory = string.IsNullOrWhiteSpace(radioExe)
                ? new DeviceInventory.Result([], "未配置后端程序路径")
                : await Task.Run(() => DeviceInventory.Read(radioExe));
            var (pnp, target) = PickTarget(devices, inventory);
            bool hasTarget = target is not null && BackendChain.ValidatedTargetSelection(target);
            int readyCards = devices.Count(d => RadioPreflight.IsSupportedHardwareId(d.HardwareId)
                && string.Equals(d.Service, "WinUSB", StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Status, "OK", StringComparison.OrdinalIgnoreCase));

            var guide = SetupGuide.Evaluate(new SetupGuide.Input(
                files, readyCards, hasTarget, authorized, recovery, session is not null));
            currentStep = guide.Step;
            ApplyGuide(guide);

            var report = ReadinessReport.Build(new ReadinessReport.Input(
                devices, pnp?.InstanceId ?? "", files, authorized, recovery));
            if (pnp is not null && target is null)
                report += "\n  ✗ 已找到 WinUSB 网卡，但无法按 USB 端口映射到 libusb 设备（可拔插后重扫）。";
            ReceiveStatus.Text = report;
        }
        catch (Exception ex)
        {
            GuideStepLabel.Text = "检查失败";
            GuideTitle.Text = "自动检查没有完成";
            GuideDetail.Text = ex.Message;
        }
    }

    // Render the guide result into the big card and highlight the current step.
    private void ApplyGuide(SetupGuideResult guide)
    {
        GuideStepLabel.Text = guide.StepLabel;
        GuideTitle.Text = guide.Title;
        GuideDetail.Text = guide.Detail;
        GuideAction.Content = string.IsNullOrEmpty(guide.PrimaryAction) ? "正在接收…" : guide.PrimaryAction;
        GuideAction.Visibility = guide.PrimaryActionVisible ? Visibility.Visible : Visibility.Collapsed;
        GuideAction.Background = guide.Step == GuideStep.SwitchDriver
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x6E, 0x4A))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x6B, 0xED));
        GuideAction.BorderBrush = GuideAction.Background;

        int active = guide.Step switch
        {
            GuideStep.SwitchDriver or GuideStep.Rescan => 1,
            GuideStep.StartReceiving => 2,
            GuideStep.Receiving => 3,
            _ => 0
        };
        HighlightStep(Step1Box, Step1Text, "1  换驱动", active, 1);
        HighlightStep(Step2Box, Step2Text, "2  开启接收", active, 2);
        HighlightStep(Step3Box, Step3Text, "3  收文件", active, 3);
    }

    private static void HighlightStep(Border box, System.Windows.Controls.TextBlock text, string label, int active, int index)
    {
        bool isActive = active == index;
        bool isDone = active > index;
        box.Background = new System.Windows.Media.SolidColorBrush(isActive
            ? System.Windows.Media.Color.FromRgb(0xEA, 0xF0, 0xFF)
            : isDone ? System.Windows.Media.Color.FromRgb(0xE7, 0xF6, 0xEE) : System.Windows.Media.Color.FromRgb(0xF0, 0xF2, 0xF6));
        text.Foreground = new System.Windows.Media.SolidColorBrush(isActive
            ? System.Windows.Media.Color.FromRgb(0x2C, 0x6B, 0xED)
            : isDone ? System.Windows.Media.Color.FromRgb(0x1B, 0x6E, 0x4A) : System.Windows.Media.Color.FromRgb(0x95, 0xA1, 0xB5));
        text.Text = (isDone ? "✓ " : "") + label;
    }

    // The single main button dispatches to the right action for this step.
    private async void GuideAction_Click(object sender, RoutedEventArgs e)
    {
        switch (currentStep)
        {
            case GuideStep.SwitchDriver:
                Setup_Click(sender, e);
                break;
            case GuideStep.StartReceiving:
                StartReceive_Click(sender, e);
                break;
            case GuideStep.EnvironmentBlocked:
            case GuideStep.NeedRestart:
            case GuideStep.Rescan:
            default:
                await CheckReadinessAsync();
                break;
        }
    }

    private void ToggleDetails_Click(object sender, RoutedEventArgs e)
    {
        detailsVisible = !detailsVisible;
        ReceiveStatus.Visibility = detailsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    // Show an operation result where the user can actually see it. The details
    // box is hidden by default, so writing to Text alone looks like "nothing happened".
    private void ShowDetails(string message)
    {
        ReceiveStatus.Text = message;
        detailsVisible = true;
        ReceiveStatus.Visibility = Visibility.Visible;
    }

    private async void CheckReadiness_Click(object sender, RoutedEventArgs e) => await CheckReadinessAsync();

    // Even when enabled, starting still goes through ReceiverSession ->
    // BackendChain, whose ValidatedTargetSelection() safety lock stays closed.
    private async void StartReceive_Click(object sender, RoutedEventArgs e)
    {
        ReceiveStatus.Text = "正在启动接收链路…";
        try
        {
            var devices = await QueryRadioDevicesAsync();
            var target = await Task.Run(() => DeviceInventory.Read(Environment.GetEnvironmentVariable("DROPLINK_RADIO_EXE") ?? ""));
            var (pnp, selectedTarget) = PickTarget(devices, target);
            if (pnp is null || selectedTarget is null)
                throw new InvalidOperationException("无法定位目标网卡：需要至少一块 0BDA:C811 已绑定 WinUSB 且能被 libusb 按 USB 端口识别。请点上方主按钮“一键换 WinUSB”完成换驱动，然后重新检查。");
            var files = new ChainFiles(
                Environment.GetEnvironmentVariable("DROPLINK_RADIO_EXE") ?? "",
                Environment.GetEnvironmentVariable("DROPLINK_BRIDGE_EXE") ?? "",
                Environment.GetEnvironmentVariable("DROPLINK_NETD_EXE") ?? "",
                Environment.GetEnvironmentVariable("DROPLINK_AWDLD_EXE") ?? "");
            await StartSessionAsync(files, devices, pnp.InstanceId,
                EnvFlag("DROPLINK_AUTHORIZED"), EnvFlag("DROPLINK_RECOVERY_VERIFIED"),
                Environment.GetEnvironmentVariable("DROPLINK_PYTHON") ?? "python",
                Environment.GetEnvironmentVariable("DROPLINK_ENGINE_SCRIPT") ?? "",
                Environment.GetEnvironmentVariable("DROPLINK_OUTPUT_DIR") ?? "",
                Environment.GetEnvironmentVariable("DROPLINK_KEYS_DIR") ?? "",
                Environment.GetEnvironmentVariable("DROPLINK_DEVICE_NAME") ?? "DropLink",
                selectedTarget);
            await CheckReadinessAsync(); // advance the guide to "receiving" (step 3)
        }
        catch (Exception ex) { ReceiveStatus.Text = "启动失败：" + ex.Message; }
    }

    // Driver rollback changes the system binding, so it is never implicit.
    // It requires an explicit in-app confirmation, then launches the backed-up
    // rollback script elevated (UAC). The script itself asks for "RESTORE".
    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        var script = Environment.GetEnvironmentVariable("DROPLINK_ROLLBACK_CMD") ?? "";
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
        {
            MessageBox.Show(this,
                "未找到回退脚本。请通过“启动软件.cmd”启动本程序（它会设置脚本路径），" +
                "或确认备份仍在 work\\_backup\\droplink-driver-20261124\\回退驱动.cmd。",
                "无法回退", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(this,
            "恢复网卡为原厂 Realtek 驱动。\n\n" +
            "接下来会弹管理员授权，请点“是”；\n" +
            "然后在新窗口输入 RESTORE 回车。\n\n确定继续？",
            "回退驱动", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"\"{script}\"");
            Process.Start(psi);
            ShowDetails("已请求管理员权限。请在弹出的黑色窗口点“是”，再输入 RESTORE 回车。");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "无法启动回退脚本：" + ex.Message +
                "\n\n请手动右键运行：work\\_backup\\droplink-driver-20261124\\回退驱动.cmd，选“以管理员身份运行”。",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // One-time driver setup changes the system binding, so it is never implicit.
    // Confirm, then launch the backed-up switch script elevated (UAC). The
    // script itself imports the signing certificate and asks for "SWITCH".
    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        var script = Environment.GetEnvironmentVariable("DROPLINK_SETUP_CMD") ?? "";
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
        {
            MessageBox.Show(this,
                "未找到换驱动脚本。请通过“启动软件.cmd”启动本程序（它会设置脚本路径），" +
                "或确认备份仍在 work\\_backup\\droplink-driver-20261124\\一键换WinUSB.cmd。",
                "无法继续", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = MessageBox.Show(this,
            "切换网卡到 WinUSB（接收 AirDrop 必需，一次性）。\n\n" +
            "接下来会弹管理员授权，请点“是”；\n" +
            "然后在新窗口输入 SWITCH 回车。\n\n" +
            "随时可点红色“回退驱动”还原。确定继续？",
            "一键换 WinUSB", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"\"{script}\"");
            var proc = Process.Start(psi);
            ShowDetails("已请求管理员权限。请在弹出的黑色窗口点“是”，再输入 SWITCH 回车。\n\n" +
                "如果没弹出窗口，说明系统的安全软件拦截了提权——请手动右键脚本“以管理员身份运行”。");
            _ = proc;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "无法启动换驱动脚本：" + ex.Message +
                "\n\n请手动右键运行：work\\_backup\\droplink-driver-20261124\\一键换WinUSB.cmd，选“以管理员身份运行”。",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Pick a usable radio: any supported, healthy card bound to WinUSB is a
    // legitimate drive target. Two identical C811 cards are fine because each
    // PnP port pins a concrete libusb device, and radio opens strictly by
    // bus + dotted port path. All inputs are read-only snapshots.
    private static (RadioDevice? Device, UsbDeviceSnapshot? Target) PickTarget(
        IReadOnlyList<RadioDevice> devices, DeviceInventory.Result inventory)
    {
        var ready = devices.Where(d => RadioPreflight.IsSupportedHardwareId(d.HardwareId)
            && string.Equals(d.Service, "WinUSB", StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Status, "OK", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.LocationInfo, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ready.Length == 0) return (null, null);
        if (inventory.Error is not null) return (ready[0], null);

        // Require instance-level uniqueness: the PnP port must match exactly
        // one supported libusb device before we claim a concrete target.
        foreach (var pnp in ready)
        {
            if (!UsbTopology.TryParseLocation(pnp.LocationInfo, out var port, out _)) continue;
            var match = inventory.Devices.Where(d => RadioPreflight.IsSupportedUsbId(d.Vid, d.Pid)
                && UsbTopology.TryLastPort(d.PortPath, out var last) && last == port).ToArray();
            if (match.Length == 1) return (pnp, match[0]);
        }
        // A WinUSB card exists but cannot be pinned to libusb: keep the button
        // disabled while surfacing the card in the readiness diagnostic.
        return (ready[0], null);
    }

    private static bool EnvFlag(string name) =>
        Environment.GetEnvironmentVariable(name) is string v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static async Task<IReadOnlyList<RadioDevice>> QueryRadioDevicesAsync()
    {
        // Query-only: reads PnP instance id, hardware id, driver service and status.
        const string script = "Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match '^USB\\\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}' } | ForEach-Object { $id=$_.InstanceId; $svc=(Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data; $loc=(Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_LocationInfo' -ErrorAction SilentlyContinue).Data; [pscustomobject]@{ InstanceId=$id; Hardware=($id -replace '^.*?(VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&MI_[0-9A-F]{2})?).*','USB\\$1'); Service=[string]$svc; Status=$_.Status; Location=[string]$loc } } | ConvertTo-Json -Compress";
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动设备查询。");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("设备查询超时。"); }
        var json = await stdout; _ = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("设备查询失败。");
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : new[] { doc.RootElement };
        var result = new List<RadioDevice>();
        foreach (var item in items)
        {
            var hardware = item.GetProperty("Hardware").GetString() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(hardware, @"^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(&MI_[0-9A-F]{2})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            var location = item.TryGetProperty("Location", out var locProp) ? locProp.GetString() ?? "" : "";
            result.Add(new RadioDevice(item.GetProperty("InstanceId").GetString() ?? "", hardware,
                item.GetProperty("Service").GetString() ?? "", item.GetProperty("Status").GetString() ?? "", location));
        }
        return result;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    private async Task ScanAsync()
    {
        Devices.Text = "正在只读扫描设备…";
        try
        {
            // Query only; no instance IDs, serial numbers or arbitrary subprocess input are emitted.
            const string script = "Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match '^USB\\\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}' } | ForEach-Object { $id=$_.InstanceId; [pscustomobject]@{ Name=$_.FriendlyName; Status=$_.Status; Hardware=($id -replace '^.*?(VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&MI_[0-9A-F]{2})?).*','$1') } } | ConvertTo-Json -Compress";
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动设备查询。");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("设备查询超时。"); }
            var json = await stdout; _ = await stderr;
            if (process.ExitCode != 0) throw new InvalidOperationException("设备查询失败。");
            if (string.IsNullOrWhiteSpace(json)) { Devices.Text = "未发现 USB 设备。"; return; }
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : new[] { doc.RootElement };
            var lines = new List<string>();
            foreach (var item in items)
            {
                var hardware = item.GetProperty("Hardware").GetString() ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(hardware, @"^VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(&MI_[0-9A-F]{2})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                var name = item.GetProperty("Name").GetString();
                var hint = hardware.StartsWith("VID_0BDA&PID_C820", StringComparison.OrdinalIgnoreCase) || hardware.StartsWith("VID_0BDA&PID_C811", StringComparison.OrdinalIgnoreCase) ? "上游支持的 RTL8821C 芯片（需 WinUSB 绑定）" : "未确认兼容性";
                lines.Add($"{name ?? "未命名设备"}  ·  {hardware}\n{hint}");
            }
            // Optional read-only libusb view. Enabled only when a radio path is
            // configured via DROPLINK_RADIO_EXE; --list-devices never opens or
            // resets a device. This supplies bus/port data for topology mapping
            // but does NOT select a device, start the radio or enable receiving.
            var radioExe = Environment.GetEnvironmentVariable("DROPLINK_RADIO_EXE");
            if (!string.IsNullOrWhiteSpace(radioExe))
            {
                var inventory = await Task.Run(() => DeviceInventory.Read(radioExe));
                if (inventory.Error is null && inventory.Devices.Count > 0)
                {
                    lines.Add("— libusb 视角（只读枚举，未选中任何设备）—");
                    foreach (var d in inventory.Devices)
                        lines.Add($"bus {d.Bus} · port {(string.IsNullOrEmpty(d.PortPath) ? "(根端口)" : d.PortPath)} · {d.Vid:x4}:{d.Pid:x4}");
                }
                else if (inventory.Error is not null)
                {
                    lines.Add("libusb 枚举不可用：" + inventory.Error);
                }
            }
            Devices.Text = lines.Count == 0 ? "未发现可识别的 USB 硬件 ID。" : string.Join("\n\n", lines);
        }
        catch (Exception ex) { Devices.Text = "扫描不可用：" + ex.Message; }
    }
}
