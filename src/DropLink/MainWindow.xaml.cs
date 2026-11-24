using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
namespace DropLink;
public partial class MainWindow : Window
{
    private readonly TransferState transferState = new();
    private ReceiverSession? session;
    private TrayIcon? tray;
    private bool exitRequested;
    private GuideStep currentStep = GuideStep.EnvironmentBlocked;
    private bool detailsVisible;
    // Transient status ("正在初始化网卡…") lives on the progress line, not in the
    // details box, so the details box can stay collapsed until the user asks.
    private bool transientProgress;
    private bool initialized;
    // True when the process was launched with --tray by the logon task: the
    // window stays hidden, so the receiver has to start itself.
    internal bool BackgroundMode { get; set; }
    public MainWindow()
    {
        InitializeComponent();
        // Auto-check on launch so the guide is useful the moment the window opens.
        Loaded += async (_, _) => await InitializeAsync();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += async (_, _) => await CloseSessionAsync();
        // Windows logoff/shutdown must not be blocked by close-to-tray.
        System.Windows.Application.Current.SessionEnding += (_, _) => exitRequested = true;
        // Create the tray before the first Show so autostart can stay hidden.
        try { EnsureTray(); } catch (Exception) { /* tray is best-effort */ }
    }

    // Runs once whether the window is shown or not. A tray-only launch calls
    // this from App because Loaded never fires for a window that never shows.
    internal async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        try { EnsureTray(); } catch (Exception) { /* tray is best-effort */ }
        await CheckReadinessAsync();
        if (!BackgroundMode || session is not null) return;
        // At logon the USB card may not be enumerated yet, so give it a bounded
        // window to show up instead of failing the whole background start once.
        for (var attempt = 0; attempt < 3 && session is null; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                await CheckReadinessAsync();
            }
            if (currentStep != GuideStep.StartReceiving) continue;
            // Logon start is meant to be hands-off: come up already receiving.
            await StartReceiveAsync(silent: true);
        }
    }

    private void EnsureTray() => tray ??= new TrayIcon(this, RequestExit);

    // A second launch signals this window back; tray double-click uses the same path.
    public void ShowFromTray()
    {
        if (tray is not null) tray.ShowWindow();
        else
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && tray is not null) tray.HideWindow();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exitRequested) return;
        // If the tray could not be created, let the window close normally
        // instead of hiding it with no way to bring it back.
        if (tray is null) { exitRequested = true; return; }
        e.Cancel = true;
        tray.HideWindow();
    }

    private async void RequestExit()
    {
        if (exitRequested) return;
        exitRequested = true;
        try { await CloseSessionAsync(); }
        catch (Exception) { /* exit anyway */ }
        tray?.Dispose();
        tray = null;
        System.Windows.Application.Current.Shutdown();
    }

    // A future validated chain coordinator calls this; nothing starts at launch.
    // The receive button stays disabled until device selection and recovery are proven.
    private async Task StartSessionAsync(
        ChainFiles files, IReadOnlyList<RadioDevice> devices, string selectedInstance,
        bool authorized, bool recoveryVerified,
        string python, string engineScript, string outputDirectory, string keysDirectory, string deviceName,
        UsbDeviceSnapshot? selectedTarget = null, bool allowLegacy = false)
    {
        if (session is not null) throw new InvalidOperationException("接收会话已经启动。");
        var created = new ReceiverSession();
        created.EngineEvent += OnEngineEvent;
        created.Failed += OnSessionFailed;
        session = created;
        try
        {
            await created.StartAsync(files, devices, selectedInstance, authorized, recoveryVerified,
                python, engineScript, outputDirectory, keysDirectory, deviceName, selectedTarget, allowLegacy);
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

    // A dead chain must release the session, otherwise the guide keeps showing
    // "正在接收" with nothing running and the button refuses to restart.
    private void OnSessionFailed(string message) =>
        _ = Dispatcher.BeginInvoke(async () =>
        {
            // A background receiver has no visible window to point at; a modal
            // box at logon would be worse than a tray notice.
            if (!IsVisible) tray?.Notify("DropLink 接收链路异常", message);
            else MessageBox.Show(this, message, "接收链路异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            try { await CloseSessionAsync(); await CheckReadinessAsync(); }
            catch (Exception) { /* refresh is best-effort */ }
        });

    // Engine facts are applied on the UI thread; an 'asked' event triggers an
    // explicit confirmation whose answer is sent back through the session.
    private void OnEngineEvent(JsonElement message)
    {
        _ = Dispatcher.BeginInvoke(async () =>
        {
            var row = transferState.Apply(message);
            RenderTransfer();
            if (!message.TryGetProperty("event", out var kind)) return;
            var name = kind.GetString();
            if (name == "receiving" && message.TryGetProperty("on", out var on))
                tray?.SetReceiving(on.GetBoolean());
            if (name != "asked" || row is null) return;
            var answer = MessageBox.Show(this, $"{row.Sender} 想发送：\n{row.Files}\n\n是否接收？",
                "接收请求", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            try { await (session?.AnswerAsync(row.Id, answer == MessageBoxResult.Yes) ?? Task.CompletedTask); }
            catch (Exception) { row.Status = "无法发送答复"; }
        });
    }

    // A single compact line replaces the old transfer list: it appears only
    // while a transfer is in flight and disappears once nothing is pending.
    private void RenderTransfer()
    {
        var row = transferState.Items.FirstOrDefault();
        if (row is null) return;
        transientProgress = false; // a real transfer owns this line now
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = string.IsNullOrEmpty(row.Progress)
            ? $"{row.Status} · {row.Files}"
            : $"{row.Status} · {row.Files}  {row.Progress}";
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
            var (pnp, target, legacy) = PickTarget(devices, inventory);
            bool hasTarget = target is not null && BackendChain.ValidatedTargetSelection(target);
            hasTarget |= legacy && pnp is not null;
            int readyCards = devices.Count(d => RadioPreflight.IsSupportedHardwareId(d.HardwareId)
                && string.Equals(d.Service, "WinUSB", StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Status, "OK", StringComparison.OrdinalIgnoreCase));

            var guide = SetupGuide.Evaluate(new SetupGuide.Input(
                files, readyCards, hasTarget, authorized, recovery, session is not null));
            currentStep = guide.Step;
            ApplyGuide(guide);

            var report = ReadinessReport.Build(new ReadinessReport.Input(
                devices, pnp?.InstanceId ?? "", files, authorized, recovery));
            if (pnp is not null && target is null && !legacy)
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
        // A transient status has served its purpose once the guide moved on;
        // leaving it behind would look like the app is still busy.
        if (transientProgress)
        {
            transientProgress = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ProgressText.Text = "";
        }
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
        DetailsToggle.Content = detailsVisible ? "收起" : "详细信息";
    }

    // A status line above the links: visible while something is happening,
    // gone once it is done. It never opens the details box.
    private void ShowProgress(string message)
    {
        transientProgress = true;
        ProgressText.Text = message;
        ProgressPanel.Visibility = Visibility.Visible;
    }

    // Details stay collapsed unless the caller has to explain a failure.
    private void ShowDetails(string message, bool expand = false)
    {
        ReceiveStatus.Text = message;
        if (!expand) return;
        detailsVisible = true;
        ReceiveStatus.Visibility = Visibility.Visible;
        DetailsToggle.Content = "收起";
    }

    private async void StartReceive_Click(object sender, RoutedEventArgs e) => await StartReceiveAsync(silent: false);

    // Even when enabled, starting still goes through ReceiverSession ->
    // BackendChain, whose ValidatedTargetSelection() safety lock stays closed.
    private async Task StartReceiveAsync(bool silent)
    {
        // The radio needs ~45 s of firmware + RF init before the chain is up,
        // so say so instead of appearing frozen.
        ShowProgress("正在初始化网卡（约 40~60 秒），请不要关闭窗口…");
        try
        {
            var devices = await QueryRadioDevicesAsync();
            var inventory = await Task.Run(() => DeviceInventory.Read(Environment.GetEnvironmentVariable("DROPLINK_RADIO_EXE") ?? ""));
            var (pnp, selectedTarget, legacy) = PickTarget(devices, inventory);
            if (pnp is null)
                throw new InvalidOperationException("还没定位到网卡，先完成第 1 步换驱动。");
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
                selectedTarget, legacy);
            await CheckReadinessAsync(); // advance the guide to "receiving" (step 3)
        }
        catch (Exception ex)
        {
            // A failed start must not leave a half-built session behind: that is
            // what made the next click report "接收会话已经启动" while no backend
            // process was actually running.
            await CloseSessionAsync();
            var reason = "启动失败：" + ex.Message;
            await CheckReadinessAsync(); // back to step 2 so the button works again
            ShowDetails(reason, expand: true); // keep the reason visible after the refresh
            // A hidden receiver must not open a modal box at logon; the tray is
            // the only thing the user can see, so report it there.
            if (silent) tray?.Notify("DropLink 没能开始接收", ex.Message);
        }
    }

    // Driver rollback goes through the same confirmed, elevated, self-refreshing
    // path as the switch; the difference is only which script it runs.
    private void Rollback_Click(object sender, RoutedEventArgs e) => SwitchDriver(switchToWinUsb: false);

    private void Setup_Click(object sender, RoutedEventArgs e) => SwitchDriver(switchToWinUsb: true);

    // Both driver actions run the same external script and then refresh the
    // guide by themselves, so the user never has to press "check" afterwards.
    private async void SwitchDriver(bool switchToWinUsb)
    {
        var variable = switchToWinUsb ? "DROPLINK_SETUP_CMD" : "DROPLINK_ROLLBACK_CMD";
        var script = Environment.GetEnvironmentVariable(variable) ?? "";
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
        {
            MessageBox.Show(this, switchToWinUsb ? "未找到换驱动脚本。" : "未找到回退脚本。",
                "无法继续", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var question = switchToWinUsb
            ? "把网卡切换到 WinUSB（接收隔空投送必需，一次性）。\n\n全自动，大约 10~60 秒，中途不用管。\n随时可以用「回退驱动」还原。\n\n继续？"
            : "把网卡恢复成原厂驱动。\n\n全自动，大约 10~30 秒。\n\n继续？";
        if (MessageBox.Show(this, question, switchToWinUsb ? "一键换 WinUSB" : "回退驱动",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            // The script host only accepts .ps1 paths. If a .cmd/.bat wrapper was
            // configured, run the sibling .ps1 instead so -Force still applies
            // and stdout/stderr stay capturable.
            script = ResolvePowerShellScript(script);

            // Run the script directly (no cmd.exe hop) and capture its output.
            // If the app somehow lost elevation, the script says so on stdout
            // instead of failing silently.
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory
            };
            foreach (var arg in new[]
                     {
                         "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Force"
                     })
                psi.ArgumentList.Add(arg);

            ShowProgress(switchToWinUsb
                ? "正在切换驱动，请稍等（最多 1 分钟，完成后会自动刷新）…"
                : "正在恢复驱动，请稍等（完成后会自动刷新）…");
            GuideAction.IsEnabled = false;

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动脚本。");
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            GuideAction.IsEnabled = true;
            await CheckReadinessAsync();
            if (currentStep == GuideStep.SwitchDriver || switchToWinUsb == false)
            {
                var detail = new System.Text.StringBuilder();
                detail.AppendLine($"驱动操作没有成功（退出码 {process.ExitCode}）。");
                if (!string.IsNullOrWhiteSpace(stdout)) detail.AppendLine().AppendLine(stdout.Trim());
                if (!string.IsNullOrWhiteSpace(stderr)) detail.AppendLine().AppendLine(stderr.Trim());
                detail.AppendLine().AppendLine(ReadLogTail(Path.Combine(Path.GetDirectoryName(script) ?? "", "setup.log")));
                ShowDetails(detail.ToString().Trim(), expand: true);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动失败：" + ex.Message + "\n\n请右键以管理员身份运行：\n" + script,
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string ResolvePowerShellScript(string path)
    {
        if (path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) return path;
        var sibling = Path.ChangeExtension(path, ".ps1");
        return File.Exists(sibling) ? sibling : path;
    }

    // The switch script logs every stage; surfacing the tail here means a
    // failed step shows the reason inside the app instead of in a log file
    // nobody opens.
    private static string ReadLogTail(string path, int lines = 12)
    {
        try
        {
            if (!File.Exists(path)) return "（没有生成日志，脚本可能没能启动）";
            var all = File.ReadAllLines(path);
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex) { return "（日志读取失败：" + ex.Message + "）"; }
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // Pick a usable radio: any supported, healthy card bound to WinUSB is a
    // legitimate drive target. Two identical C811 cards are fine because each
    // PnP port pins a concrete libusb device, and radio opens strictly by
    // bus + dotted port path. All inputs are read-only snapshots.
    private static (RadioDevice? Device, UsbDeviceSnapshot? Target, bool Legacy) PickTarget(
        IReadOnlyList<RadioDevice> devices, DeviceInventory.Result inventory)
    {
        var ready = devices.Where(d => RadioPreflight.IsSupportedHardwareId(d.HardwareId)
            && string.Equals(d.Service, "WinUSB", StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.Status, "OK", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.LocationInfo, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ready.Length == 0) return (null, null, false);
        // The bundled backend predates --list-devices, so there is no libusb
        // inventory to map. Preflight already guarantees exactly one supported
        // card, so a VID/PID open can still only reach that card.
        if (inventory.Error is not null)
            return ready.Length == 1 ? (ready[0], null, true) : (ready[0], null, false);

        // Require instance-level uniqueness: the PnP port must match exactly
        // one supported libusb device before we claim a concrete target.
        foreach (var pnp in ready)
        {
            if (!UsbTopology.TryParseLocation(pnp.LocationInfo, out var port, out _)) continue;
            var match = inventory.Devices.Where(d => RadioPreflight.IsSupportedUsbId(d.Vid, d.Pid)
                && UsbTopology.TryLastPort(d.PortPath, out var last) && last == port).ToArray();
            if (match.Length == 1) return (pnp, match[0], false);
        }
        // A WinUSB card exists but cannot be pinned to libusb: keep the button
        // disabled while surfacing the card in the readiness diagnostic.
        return (ready[0], null, false);
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
}
