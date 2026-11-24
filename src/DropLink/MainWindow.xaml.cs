using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
namespace DropLink;
public partial class MainWindow : Window
{
    private readonly TransferState transferState = new();
    private EngineClient? engine;
    public MainWindow() { InitializeComponent(); Transfers.ItemsSource = transferState.Items; Loaded += async (_, _) => await ScanAsync(); }

    // Only a future validated chain coordinator may call this; no process starts at launch.
    private void AttachEngine(string python, string script, string addressFile, string directory, string keys, string deviceName)
    {
        if (engine is not null) throw new InvalidOperationException("引擎已经启动。");
        engine = EngineClient.Start(python, script, addressFile, directory, keys, deviceName,
            OnEngineEvent, code => { _ = Dispatcher.BeginInvoke(() => MessageBox.Show(this, "接收服务已停止。")); });
    }

    // Called only by a future validated chain coordinator. No engine is started at launch.
    private void OnEngineEvent(JsonElement message)
    {
        _ = Dispatcher.BeginInvoke(async () =>
        {
            var row = transferState.Apply(message);
            if (row is null || !message.TryGetProperty("event", out var kind) || kind.GetString() != "asked") return;
            var answer = MessageBox.Show(this, $"{row.Sender} 想发送：\n{row.Files}\n\n是否接收？",
                "接收请求", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            try { if (engine is not null) await engine.AnswerAsync(row.Id, answer == MessageBoxResult.Yes); }
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
                if (!Regex.IsMatch(hardware, @"^VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(&MI_[0-9A-F]{2})?$", RegexOptions.IgnoreCase)) continue;
                var name = item.GetProperty("Name").GetString();
                var hint = hardware.StartsWith("VID_0BDA&PID_C820", StringComparison.OrdinalIgnoreCase) ? "上游有同 USB ID 的历史验证；本设备未验证" : "未确认兼容性";
                lines.Add($"{name ?? "未命名设备"}  ·  {hardware}\n{hint}");
            }
            Devices.Text = lines.Count == 0 ? "未发现可识别的 USB 硬件 ID。" : string.Join("\n\n", lines);
        }
        catch (Exception ex) { Devices.Text = "扫描不可用：" + ex.Message; }
    }
}
