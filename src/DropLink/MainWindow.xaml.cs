using System.Diagnostics;
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
                var hint = hardware.StartsWith("VID_0BDA&PID_C820", StringComparison.OrdinalIgnoreCase) ? "上游有同 USB ID 的历史验证；本设备未验证" : "未确认兼容性";
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
