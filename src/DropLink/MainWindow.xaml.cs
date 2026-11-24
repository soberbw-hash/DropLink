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
    private SettingsWindow? settingsWindow;
    private bool exitRequested;
    private GuideStep currentStep = GuideStep.EnvironmentBlocked;
    private bool detailsVisible;
    // Transient status ("正在初始化网卡…") lives on the progress line, not in the
    // details box, so the details box can stay collapsed until the user asks.
    private bool transientProgress;
    private bool initialized;
    // The receiver is meant to be always-on: it starts itself on launch and
    // restarts itself when the chain dies, so the user never has to click.
    private int restartAttempts;
    private bool restartScheduled;
    private string lastStage = "";
    // True from the moment a start attempt begins until it succeeds or fails.
    // Without it, the "开启接收" button stayed live during a start and a second
    // press launched a second chain on top of the first.
    private bool starting;
    private bool spinnerRunning;
    private bool pulseRunning;
    private System.Windows.Media.Animation.Storyboard? pulse;
    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowIcon();
        // Auto-check on launch so the guide is useful the moment the window opens.
        Loaded += async (_, _) => await InitializeAsync();
        Closing += OnClosing;
        Closed += async (_, _) =>
        {
            // The tray must never outlive the window. A notification-area icon
            // whose window is gone is the "ghost app" the user could not get
            // rid of: it looks alive, answers nothing, and only disappears
            // when the mouse happens to pass over it.
            //
            // Guarded per step: this is an async void handler, so a throw here
            // becomes an app-level error box at the worst possible moment.
            try { tray?.Dispose(); }
            catch (Exception error) { AppLog.WriteException("closed: tray", error); }
            tray = null;
            try { await CloseSessionAsync(); }
            catch (Exception error) { AppLog.WriteException("closed: session", error); }
        };
        // Windows logoff/shutdown must not be blocked by close-to-tray.
        System.Windows.Application.Current.SessionEnding += (_, _) =>
        {
            exitRequested = true;
            App.ShuttingDown = true;
        };
        // Create the tray before the first Show so autostart can stay hidden.
        try { EnsureTray(); } catch (Exception) { /* tray is best-effort */ }
    }

    // The title bar and the taskbar button both take the window icon. WPF falls
    // back to the executable's icon when this is unset, but "falls back" is the
    // kind of assumption that makes an icon change look like it did not happen,
    // so the same app.ico the exe carries is applied explicitly. A failure here
    // must not stop the receiver from starting.
    private void ApplyWindowIcon()
    {
        try
        {
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("app.ico");
            if (stream is not null)
            {
                // Pick the largest frame: the first one is 16x16, which looks
                // soft in the title bar at 100% scaling.
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                return;
            }
        }
        catch (Exception error)
        {
            AppLog.Write("window icon from resource failed: " + error.Message);
        }
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (extracted is null) return;
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                extracted.Handle, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch (Exception error)
        {
            AppLog.Write("window icon from exe failed: " + error.Message);
        }
    }

    // Runs once whether the window is shown or not. A tray-only launch calls
    // this from App because Loaded never fires for a window that never shows.
    internal async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        SetStatus(LinkStatus.Working, "正在检查设备…");
        try { EnsureTray(); } catch (Exception) { /* tray is best-effort */ }
        await CheckReadinessAsync();
        if (session is not null) return;
        // Always-on receiver: start without being asked, whichever way it was
        // launched. It only starts when the guide already says it is ready, so a
        // machine without the card or driver still shows step 1/2 instead.
        // At logon the USB card may not be enumerated yet, so give it a bounded
        // window to show up instead of failing the whole start once.
        for (var attempt = 0; attempt < 3 && session is null; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                await CheckReadinessAsync();
            }
            if (currentStep != GuideStep.StartReceiving) continue;
            await StartReceiveAsync(silent: true);
        }
        // Tell the user it is live, otherwise a silent logon start is invisible.
        if (session is not null && !IsVisible)
            tray?.Notify("DropLink 已在后台接收",
                "iPhone 隔空投送里选这台电脑就能投，收到文件会在右下角提示。");    }

    private void EnsureTray() => tray ??= new TrayIcon(this, RequestExit, OpenSettingsWindow);

    /// <summary>
    /// Settings open inside the app. It used to launch settings-gui.ps1, which
    /// flashed a PowerShell console and then a second form built with absolute
    /// coordinates; this is the same data\settings.json edited by a normal page.
    /// </summary>
    internal void OpenSettingsWindow()
    {
        if (settingsWindow is not null)
        {
            if (settingsWindow.WindowState == WindowState.Minimized)
                settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
            return;
        }
        var page = new SettingsWindow(ApplyDeviceNameAsync);
        // CenterOwner is wrong when the main window was never shown (the logon
        // scheduled task starts with --tray): the page would be positioned off an
        // invisible window. Only own it when it is actually on screen.
        if (IsVisible)
        {
            page.Owner = this;
            page.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else page.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        page.Closed += (_, _) => settingsWindow = null;
        settingsWindow = page;
        page.Show();
        page.Activate();
    }

    /// <summary>
    /// Pushes a new device name to the receiver that is already running. Renaming
    /// only on the next start would leave the phone showing the old name with
    /// nothing explaining why, so this is applied live when possible. Returns
    /// false when there is nothing running to rename - the settings file is
    /// already written, so that is "next start", not a failure.
    /// </summary>
    private async Task<bool> ApplyDeviceNameAsync(string name)
    {
        var current = session;
        if (current is null) return false;
        await current.SetNameAsync(name);
        return true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

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

    // The one place that decides what the app claims about itself. The dot and
    // line in the header, the window title (which is also the taskbar button's
    // tooltip) and the tray tooltip all read from here, so they cannot end up
    // disagreeing about whether anything is running.
    private enum LinkStatus { Idle, Working, Ready, Receiving, Failed }

    private void SetStatus(LinkStatus kind, string text, string hint = "")
    {
        var color = kind switch
        {
            LinkStatus.Receiving => System.Windows.Media.Color.FromRgb(0x1B, 0x6E, 0x4A),
            LinkStatus.Ready => System.Windows.Media.Color.FromRgb(0x2C, 0x6B, 0xED),
            LinkStatus.Working => System.Windows.Media.Color.FromRgb(0xC2, 0x7A, 0x12),
            LinkStatus.Failed => System.Windows.Media.Color.FromRgb(0xB2, 0x3A, 0x48),
            _ => System.Windows.Media.Color.FromRgb(0x95, 0xA1, 0xB5)
        };
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(color);
        StatusText.Text = text;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(kind == LinkStatus.Idle
            ? System.Windows.Media.Color.FromRgb(0x52, 0x62, 0x7C)
            : color);
        StatusHint.Text = hint;
        StatusHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        Title = "DropLink · " + text;
        tray?.SetStatus(text);
        // A dot that breathes while the app is working is the cheapest honest
        // "still busy" signal. A static dot beside "正在初始化" looks frozen.
        // Stop() is only legal after a controllable Begin(), hence the flag.
        if (kind == LinkStatus.Working)
        {
            if (!pulseRunning)
            {
                pulse ??= (System.Windows.Media.Animation.Storyboard)FindResource("PulseDot");
                pulse.Begin(this, true);
                pulseRunning = true;
            }
        }
        else if (pulseRunning)
        {
            pulse?.Stop(this);
            pulseRunning = false;
            StatusDot.Opacity = 1;
        }
    }

    // Rotating ring beside the card title. Started once and left running while
    // visible: it is composited off the UI thread, so a hidden ring costs
    // nothing, and it never claims progress that is not happening because it is
    // only shown for the one state that is genuinely in progress.
    private void SetSpinning(bool on)
    {
        Spinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on == spinnerRunning) return;
        spinnerRunning = on;
        if (!on)
        {
            SpinnerAngle.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
            return;
        }
        SpinnerAngle.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            });
    }

    // "未在接收" on its own only restates the problem, so the hint says which
    // of the three setup steps is still open.
    private void SetIdleStatus()
    {
        var (hint, kind) = currentStep switch
        {
            GuideStep.SwitchDriver => ("网卡还在原厂驱动上，第 1 步没做完", LinkStatus.Idle),
            GuideStep.Rescan => ("网卡已换好驱动，还差拔插重扫", LinkStatus.Idle),
            GuideStep.NeedRestart => ("重新打开一次 DropLink 就会继续", LinkStatus.Idle),
            GuideStep.EnvironmentBlocked => ("后端组件缺失，见下方详细信息", LinkStatus.Failed),
            GuideStep.StartReceiving => ("条件已满足，马上自动开启", LinkStatus.Idle),
            GuideStep.Starting => ("正在初始化网卡，通常 1 分钟", LinkStatus.Working),
            _ => ("", LinkStatus.Idle)
        };
        SetStatus(kind, "未在接收", hint);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exitRequested) return;
        // Closing the window used to hide it in the tray, so × did not do what ×
        // does everywhere else: the receiver kept running with nothing on screen
        // saying so. × now quits; the tray is a shortcut, not a hiding place.
        if (transferState.Items.Any(row => row.Receiving && !row.Finished))
        {
            var answer = MessageBox.Show(this,
                "正在接收文件。现在退出会中断这次传输。\n\n仍要退出？",
                "退出 DropLink", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        // One exit path. Shutdown closes this window again with exitRequested
        // set, so this does not recurse.
        e.Cancel = true;
        RequestExit();
    }

    private async void RequestExit()
    {
        if (exitRequested) return;
        exitRequested = true;
        App.ShuttingDown = true;
        // Last resort: a shutdown that hangs on a wedged backend or a tray that
        // will not let go leaves a process with no window and no way back in -
        // the single-instance lock then makes every later launch do nothing.
        // That is the "I closed it and it is still running" complaint again, so
        // the process is forced out if the orderly path has not finished.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            Environment.Exit(0);
        });
        // Every step is guarded on its own. This used to be one try around the
        // session only, so anything thrown later - closing the tray icon, or
        // Shutdown itself - escaped from an async void handler, surfaced as the
        // "DropLink 异常" box with no explanation, and left the window on screen
        // after the user had already said close.
        try { SetStatus(LinkStatus.Idle, "正在退出…"); }
        catch (Exception error) { AppLog.WriteException("close: status", error); }
        try { await CloseSessionAsync(); }
        catch (Exception error) { AppLog.WriteException("close: session", error); }
        try { tray?.Dispose(); }
        catch (Exception error) { AppLog.WriteException("close: tray", error); }
        tray = null;
        try { System.Windows.Application.Current.Shutdown(); }
        catch (Exception error)
        {
            AppLog.WriteException("close: shutdown", error);
            Environment.Exit(0);
        }
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
        created.Stage += OnSessionStage;
        created.Notice += OnSessionNotice;
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
            // Already shutting down: a modal box here would hold the process
            // open after the user asked it to close.
            if (exitRequested) return;
            // A background receiver has no visible window to point at; a modal
            // box at logon would be worse than a tray notice.
            if (!IsVisible) tray?.Notify("DropLink 接收链路异常", message);
            else MessageBox.Show(this, message, "接收链路异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            try { await CloseSessionAsync(); await CheckReadinessAsync(); }
            catch (Exception) { /* refresh is best-effort */ }
            // The refresh above would report a calm "未在接收"; the reason is
            // what the user needs to see, and it is the only thing that makes
            // the automatic retry below understandable.
            SetStatus(LinkStatus.Failed, "接收中断", message);
            // Bring the receiver back by itself: the point of running in the
            // tray is that the user never has to notice it stopped.
            ScheduleRestart(message);
        });

    // Keeps the receiver alive without user action. Backs off, then gives up
    // after a handful of tries so an unplugged card does not churn forever.
    private void ScheduleRestart(string reason)
    {
        if (exitRequested || restartScheduled) return;
        restartAttempts++;
        if (restartAttempts > 6)
        {
            AppLog.Write("auto restart gave up: " + reason);
            tray?.Notify("DropLink 已停止自动重试", "连续多次没能启动接收，点开界面看看原因。");
            return;
        }
        restartScheduled = true;
        var delay = TimeSpan.FromSeconds(Math.Min(60, 10 * restartAttempts));
        AppLog.Write($"auto restart #{restartAttempts} in {delay.TotalSeconds:0}s: {reason}");
        _ = RestartAfterDelayAsync(delay);
    }

    private async Task RestartAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            // Release the slot before any long wait so a failure inside the
            // start below can still schedule the next attempt.
            restartScheduled = false;
            if (exitRequested || session is not null) return;
            await CheckReadinessAsync();
            if (exitRequested || session is not null) return;
            if (currentStep != GuideStep.StartReceiving)
            {
                // The adapter is unplugged or not mapped yet. That is a normal
                // state, not a failure: wait for it without spending attempts.
                await WaitForDeviceThenStartAsync();
                return;
            }
            await StartReceiveAsync(silent: true);
        }
        catch (Exception error)
        {
            AppLog.Write("auto restart failed: " + error.Message);
            restartScheduled = false;
        }
    }

    // Polls slowly until a usable card is back, then resumes receiving. This is
    // what makes "拔掉网卡再插回来" recover without the user touching anything.
    private async Task WaitForDeviceThenStartAsync()
    {
        AppLog.Write("waiting for a usable radio; recovery is paused, not failing");
        SetStatus(LinkStatus.Idle, "未在接收", "在等网卡插回来，插上会自动恢复，不用管它");
        while (!exitRequested && session is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            if (exitRequested || session is not null) return;
            await CheckReadinessAsync();
            if (currentStep != GuideStep.StartReceiving) continue;
            restartAttempts = 0;
            await StartReceiveAsync(silent: true);
            return;
        }
    }

    // Chain stages arrive off the UI thread; a running transfer keeps the line.
    private void OnSessionStage(string message) =>
        _ = Dispatcher.BeginInvoke(() =>
        {
            lastStage = message;
            // No duration hint here: the card owns "how long and what happens
            // next". Repeating it on both lines was the exact noise the user
            // pointed at.
            SetStatus(LinkStatus.Working, message.TrimEnd('…', '.', ' '));
            if (!transientProgress) return;
            ProgressText.Text = message;
            ProgressPanel.Visibility = Visibility.Visible;
        });

    // Recoverable events: the session healed itself, so nothing is torn down.
    private void OnSessionNotice(string message) =>
        _ = Dispatcher.BeginInvoke(() =>
        {
            AppLog.Write("notice: " + message);
            SetStatus(LinkStatus.Working, message.TrimEnd('…', '.', ' '));
            if (IsVisible)
            {
                if (transientProgress) ProgressText.Text = message;
                return;
            }
            tray?.Notify("DropLink 自动恢复", message);
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
            if (name == "ready") AppLog.Mark("engine:ready");
            if (name == "ready")
                SetStatus(LinkStatus.Ready, "已就绪，正在打开接收端口…");
            // 'receiving' is the moment an iPhone can actually see this PC, so
            // it is the state the header should be showing all day. The port
            // comes from the engine; without it the line is just a guess.
            if (name == "receiving" && message.TryGetProperty("on", out var on))
            {
                var live = on.ValueKind == JsonValueKind.True;
                var port = message.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) ? n : 0;
                if (live)
                    SetStatus(LinkStatus.Receiving, "正在接收",
                        (port > 0 ? $"端口 {port} · " : "") + "在 iPhone 隔空投送里选这台电脑");
                else
                    SetStatus(LinkStatus.Ready, "已就绪，接收端口没开",
                        "iPhone 看不到这台电脑时，点下方详细信息看原因");
            }
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
        // A refresh is not a state change: a live session keeps its own line
        // instead of flickering back to "checking" every time the guide runs,
        // and so does a start that is still in flight.
        if (session is null && !starting) SetStatus(LinkStatus.Working, "正在检查设备…");
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
                files, readyCards, hasTarget, authorized, recovery, session is not null, starting));
            currentStep = guide.Step;
            ApplyGuide(guide);

            var report = ReadinessReport.Build(new ReadinessReport.Input(
                devices, pnp?.InstanceId ?? "", files, authorized, recovery));
            if (pnp is not null && target is null && !legacy)
                report += "\n  ✗ 已找到 WinUSB 网卡，但无法按 USB 端口映射到 libusb 设备（可拔插后重扫）。";
            ReceiveStatus.Text = report;
            // The guide has spoken, so the header can too: when nothing runs,
            // say why instead of leaving the last busy message on screen. While
            // a start is in flight the header is already correct and must not
            // be talked over with "未在接收".
            if (session is null && !starting) SetIdleStatus();
        }
        catch (Exception ex)
        {
            GuideStepLabel.Text = "检查失败";
            GuideTitle.Text = "自动检查没有完成";
            GuideDetail.Text = ex.Message;
            SetStatus(LinkStatus.Failed, "检查失败", ex.Message);
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
            GuideStep.StartReceiving or GuideStep.Starting => 2,
            GuideStep.Receiving => 3,
            _ => 0
        };
        // A wait with no motion reads as a hang. The ring only turns while
        // something is genuinely in progress, so it never claims activity that
        // is not happening.
        SetSpinning(guide.Step == GuideStep.Starting);
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

    private void ClearProgress()
    {
        transientProgress = false;
        ProgressText.Text = "";
        ProgressPanel.Visibility = Visibility.Collapsed;
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
        if (exitRequested) return;
        // Something is already receiving. Starting a second one can only fight
        // the first, and the failure path below used to tear that first one
        // down - a healthy chain killed by a redundant attempt.
        if (session is not null) return;
        // One attempt at a time. The status line and the card both say "正在
        // 初始化" while this runs, and the card no longer offers a button - so a
        // second entry can only come from the auto-restart path, which must not
        // stack a second chain on top of a start that is still in flight.
        if (starting) return;
        starting = true;
        // The radio needs ~45 s of firmware + RF init before the chain is up.
        // The stage line carries the detail from here on; a second copy in the
        // progress box said the same sentence twice on the same screen.
        SetStatus(LinkStatus.Working, "正在初始化网卡");
        AppLog.Mark("session:start requested");
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
            restartAttempts = 0; // a healthy chain clears the backoff history
            await CheckReadinessAsync(); // advance the guide to "receiving" (step 3)
        }
        catch (Exception ex)
        {
            // Deliberately no CloseSessionAsync() here. StartSessionAsync already
            // disposes the session it created when its own start fails, and this
            // catch also runs for failures that have nothing to do with the
            // chain - a 15 s PnP query timeout, for one. Closing unconditionally
            // killed a receiver that was already up and working, which is what
            // made the iPhone lose sight of this PC mid-session.
            if (exitRequested) return;
            // The chain came up; only the follow-up status refresh failed. It
            // would be a lie the user can see through to print "启动失败" while
            // the receiver is running.
            if (session is not null)
            {
                AppLog.Write("post-start refresh failed: " + ex.Message);
                SetStatus(LinkStatus.Working, "正在启动接收", "状态刷新失败：" + ex.Message);
                return;
            }
            // Layered error: which stage, what happened, where to look. One
            // opaque sentence is what made every past failure a guessing game.
            var stage = string.IsNullOrWhiteSpace(lastStage)
                ? "启动准备"
                : lastStage.TrimEnd('…', '.', ' ');
            var reason = $"启动失败（阶段：{stage}）\n原因：{ex.Message}\n日志：data\\logs\\app.log";
            AppLog.Write($"start failed at [{stage}]: {ex.Message}");
            // Release the flag first: the guide has to see "not starting any
            // more", otherwise it keeps saying 正在初始化 and keeps the retry
            // button hidden after a failure that needs exactly that button.
            starting = false;
            await CheckReadinessAsync(); // back to step 2 so the button works again
            // Leaving "正在初始化网卡…" on screen after a failure is what makes a
            // failed start look like a hang. Replace it with the real reason.
            ClearProgress();
            GuideStepLabel.Text = "启动失败";
            GuideTitle.Text = $"接收没有启动起来（{stage}）";
            GuideDetail.Text = ex.Message + "\n详细日志：data\\logs\\app.log";
            ShowDetails(reason, expand: true); // keep the reason visible after the refresh
            SetStatus(LinkStatus.Failed, "接收没有启动", $"{stage}：{ex.Message}");
            // A hidden receiver must not open a modal box at logon; the tray is
            // the only thing the user can see, so report it there.
            if (silent) tray?.Notify("DropLink 没能开始接收", ex.Message);
            ScheduleRestart(ex.Message);
        }
        finally { starting = false; }
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
        // 45 s, not 15: this query competes with four backends that are still
        // initialising the radio, and a PowerShell start plus a full PnP
        // enumeration on a busy machine regularly takes longer than 15 s. The
        // old limit turned a slow diagnostic into a failed start.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
