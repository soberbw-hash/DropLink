using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
namespace DropLink;
public partial class MainWindow : Window
{
    private readonly TransferState transferState = new();
    private ReceiverSession? session;
    public MainWindow() { InitializeComponent(); Transfers.ItemsSource = transferState.Items; Loaded += async (_, _) => await ScanAsync(); Closed += async (_, _) => await CloseSessionAsync(); }

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

    // Read-only: reports what is missing instead of leaving a dead grey button.
    // Never opens a device, changes a driver or starts any backend process.
    private async void CheckReadiness_Click(object sender, RoutedEventArgs e)
    {
        ReceiveStatus.Text = "正在只读检查…";
        StartButton.IsEnabled = false;
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
            var selected = pnp?.InstanceId ?? "";
            var report = ReadinessReport.Build(new ReadinessReport.Input(devices, selected, files, authorized, recovery));
            if (target is null && pnp is not null)
                report += "\n  ✗ 已找到 WinUSB 网卡，但无法按 USB 端口把它映射到 libusb 设备（可能需重新扫描）。";
            ReceiveStatus.Text = report;
            // Enabling means all preconditions are met AND a real libusb target
            // was resolved. The chain still re-validates everything fail-closed.
            StartButton.IsEnabled = pnp is not null && target is not null &&
                BackendChain.ValidatedTargetSelection(target) && authorized && recovery &&
                files.Length == 4 && files.All(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f));
        }
        catch (Exception ex) { ReceiveStatus.Text = "检查失败：" + ex.Message; }
    }

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
                throw new InvalidOperationException("无法定位目标网卡：需要至少一块 0BDA:C811 已绑定 WinUSB 且能被 libusb 按 USB 端口识别。请先运行“一键换WinUSB.cmd”换驱动，再点“检查接收条件”。");
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
            ReceiveStatus.Text = "接收链路已启动。";
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
            "即将把 0BDA:C811 网卡从临时 WinUSB 绑定恢复为原厂 Realtek 驱动。\n\n" +
            "· 系统会弹出管理员授权（UAC）窗口，请点“是”；\n" +
            "· 随后命令行会要求输入 RESTORE 并回车确认；\n" +
            "· 恢复后该网卡可重新作为普通 Wi-Fi 使用。\n\n确定继续吗？",
            "回退驱动", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add($"\"{script}\"");
            Process.Start(psi);
            ReceiveStatus.Text = "已请求管理员权限运行回退脚本。请在弹出窗口中按提示输入 RESTORE。";
        }
        catch (Exception ex) { ReceiveStatus.Text = "无法启动回退脚本：" + ex.Message; }
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
