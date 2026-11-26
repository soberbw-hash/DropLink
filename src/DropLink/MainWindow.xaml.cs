using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;

namespace DropLink;

/// <summary>
/// The whole daily interface: what the app is doing, and what it last received.
///
/// One Render() decides everything the window says. Before, the header, the
/// card, the progress line and the details box were each written from a dozen
/// places, which is how the same screen ended up claiming "正在后台接收" and
/// "iPhone 找不到电脑" at the same time.
/// </summary>
public partial class MainWindow : Window
{
    private readonly TransferState transfers = new();
    private readonly TransferHistory history;
    private ReceiverSession? session;
    private TrayIcon? tray;
    private SettingsWindow? settingsWindow;
    private SetupWindow? setupWindow;
    private bool exitRequested;
    private bool initialized;
    private readonly SemaphoreSlim deviceChange = new(0, 1);
    private readonly DispatcherTimer topologyTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private string? activeDevice;
    private bool checkingTopology;
    private bool starting;
    private bool receiverReady;
    private long readySince;
    private TaskCompletionSource<bool> readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SetReceiverReady(bool value)
    {
        if (receiverReady == value) return;
        if (value)
        {
            readySince = Environment.TickCount64;
            readySignal.TrySetResult(true);
        }
        else
        {
            if (Environment.TickCount64 - readySince >= TimeSpan.FromMinutes(2).TotalMilliseconds)
                restartAttempts = 0;
            readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        receiverReady = value;
    }
    private bool paused;
    private int restartAttempts;
    private bool restartScheduled;
    private string lastStage = "";
    // One reason, in the user's words, for the one state that needs explaining.
    private string? failure;
    private string failureHint = "";
    private SetupGuideResult? guide;
    private GuideStep currentStep = GuideStep.Checking;
    private int supportedCards;
    private int readyCards;
    private string deviceName = "";
    private string? confirmId;
    private bool doneBanner;
    private string doneText = "";
    private DispatcherTimer? doneTimer;
    private bool spinnerOn;
    private bool breatheOn;
    private bool dotPulse;

    private enum UiState { Preparing, Waiting, Done, Transferring, Confirming, Setup, Paused, NoDevice, Failed }

    public MainWindow() : this(previewOnly: false) { }

    /// <param name="previewOnly">
    /// Dev-only: build the window without a tray, a session or an auto-start, so
    /// `--preview-main` can render one state to a PNG. It never runs in a normal
    /// launch and never talks to a backend.
    /// </param>
    internal MainWindow(bool previewOnly)
    {
        InitializeComponent();
        ApplyWindowIcon();
        deviceName = ConfiguredName();
        history = new TransferHistory(Path.Combine(AppPaths.DataDirectory(), "history.json"));
        history.Refresh();
        RecentList.ItemsSource = history.Recent;
        UpdateRecentEmpty();
        RestoreWindowBounds();
        if (previewOnly) return;
        SourceInitialized += (_, _) =>
        {
            var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)!;
            source.AddHook(DeviceMessage);
        };
        topologyTimer.Tick += async (_, _) => await TopologyChangedAsync();
        Loaded += async (_, _) => await InitializeAsync();
        Closing += OnClosing;
        Closed += async (_, _) =>
        {
            // The tray must never outlive the window: an icon whose window is
            // gone looks alive, answers nothing, and disappears only when the
            // mouse happens to pass over it.
            try { tray?.Dispose(); }
            catch (Exception error) { AppLog.WriteException("closed: tray", error); }
            tray = null;
            try { await CloseSessionAsync(); }
            catch (Exception error) { AppLog.WriteException("closed: session", error); }
        };
        System.Windows.Application.Current.SessionEnding += (_, _) =>
        {
            exitRequested = true;
            App.ShuttingDown = true;
        };
        try { EnsureTray(); } catch (Exception) { /* tray is best-effort */ }
        Render();
    }

    // ── window chrome ──────────────────────────────────────────────────────

    private void ApplyWindowIcon()
    {
        try
        {
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("app.ico");
            if (stream is not null)
            {
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                return;
            }
        }
        catch (Exception error) { AppLog.Write("window icon from resource failed: " + error.Message); }
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
        catch (Exception error) { AppLog.Write("window icon from exe failed: " + error.Message); }
    }

    private string BoundsPath => Path.Combine(AppPaths.DataDirectory(), "window.json");

    // A window that reopens off-screen after a monitor change is a window the
    // user cannot find, so the saved rectangle is clamped to the visible desktop.
    private void RestoreWindowBounds()
    {
        try
        {
            if (!File.Exists(BoundsPath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(BoundsPath));
            var root = document.RootElement;
            double Read(string name, double fallback) =>
                root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : fallback;
            var width = Math.Max(MinWidth, Read("width", Width));
            var height = Math.Max(MinHeight, Read("height", Height));
            var left = Read("left", double.NaN);
            var top = Read("top", double.NaN);
            Width = width;
            Height = height;
            if (double.IsNaN(left) || double.IsNaN(top)) return;
            var screen = SystemParameters.WorkArea;
            var visible = new Rect(screen.Left - 40, screen.Top - 10, screen.Width + 80, screen.Height + 40);
            if (!visible.Contains(new Point(left + 80, top + 20))) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        catch (Exception error) { AppLog.Write("window bounds restore failed: " + error.Message); }
    }

    private void SaveWindowBounds()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            Directory.CreateDirectory(Path.GetDirectoryName(BoundsPath)!);
            File.WriteAllText(BoundsPath, JsonSerializer.Serialize(new
            {
                left = bounds.Left, top = bounds.Top, width = bounds.Width, height = bounds.Height
            }));
        }
        catch (Exception error) { AppLog.Write("window bounds save failed: " + error.Message); }
    }

    // ── lifecycle ──────────────────────────────────────────────────────────

    internal async Task InitializeAsync()
    {
        if (initialized) return;
        initialized = true;
        // The tray-only startup never shows the window but still needs device broadcasts.
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        try { EnsureTray(); } catch (Exception) { /* tray is best-effort */ }
        await CheckReadinessAsync();
        if (session is not null) return;
        // Always-on receiver: start without being asked. At logon the USB card
        // may not be enumerated yet, so give it a bounded window to show up.
        for (var attempt = 0; attempt < 3 && session is null; attempt++)
        {
            if (attempt > 0)
            {
                await deviceChange.WaitAsync(TimeSpan.FromSeconds(20));
                await CheckReadinessAsync();
            }
            if (paused || currentStep != GuideStep.StartReceiving) continue;
            await StartReceiveAsync(silent: true);
        }
        if (receiverReady && !IsVisible)
            tray?.Notify("DropLink 已在后台接收", "iPhone 隔空投送里选这台电脑就能投。");
        // Keep watching for a card that is plugged in later. This is what makes
        // "拔了再插回来" recover without the user reopening anything.
        if (session is null && !paused) _ = WatchForDeviceAsync();
    }

    private void EnsureTray() => tray ??= new TrayIcon(this, RequestExit, OpenSettingsWindow, TogglePause, () => paused);

    internal void OpenSettingsWindow()
    {
        if (settingsWindow is not null)
        {
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
            return;
        }
        try
        {
            var page = new SettingsWindow(ApplyDeviceNameAsync, folder => ApplyEngineSettingAsync("folder", folder),
                applyAutoReceive: enabled => ApplyEngineSettingAsync("auto_receive", enabled));
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
        catch (Exception error)
        {
            // A window that cannot be constructed used to disappear into the
            // dispatcher log: the gear was clicked and nothing happened at all.
            // That is indistinguishable from a dead button, so say what happened
            // and what fixes it.
            AppLog.WriteException("settings window", error);
            failure = "设置窗口打不开";
            failureHint = "关掉 DropLink 再打开一次。";
            Render();
            tray?.Notify("设置窗口打不开", "关掉 DropLink 再打开一次。");
        }
    }

    private async Task<bool> ApplyDeviceNameAsync(string name)
    {
        deviceName = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name;
        Render();
        var current = session;
        if (current is null) return false;
        await current.SetNameAsync(deviceName);
        return true;
    }

    /// <summary>
    /// The name a phone will show for this computer.
    ///
    /// The engine prefers <c>settings.device_name</c> over the <c>--name</c> it
    /// is launched with, so a window that started from the computer name keeps
    /// showing that name after the user has renamed the receiver - the header
    /// and the AirDrop list disagree, and only one of them is telling the truth.
    /// Read the same file the engine reads, in the same order.
    /// </summary>
    private static string ConfiguredName()
    {
        var overridden = Environment.GetEnvironmentVariable("DROPLINK_DEVICE_NAME");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        var saved = SettingsFile.Text("device_name");
        return string.IsNullOrWhiteSpace(saved) ? Environment.MachineName : saved;
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private async Task<bool> ApplyEngineSettingAsync(string key, object value)
    {
        var current = session;
        if (current is null) return false;
        await current.ApplySettingAsync(key, value);
        return current == session;
    }

    // Also reached from the settings page, where clearing is deliberate and
    // final, so no undo is kept.
    private void ClearHistory()
    {
        clearedHistory = null;
        history.Clear();
        UpdateRecentEmpty();
        Render();
    }

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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowBounds();
        if (exitRequested) return;
        if (tray is null) { e.Cancel = true; RequestExit(); return; }
        e.Cancel = true;
        ShowCloseChoice();
    }

    private void ShowCloseChoice()
    {
        MainContent.IsEnabled = false;
        CloseHint.Text = transfers.Active is null ? "留在后台可继续接收文件。" : "正在接收文件，退出会中断本次传输。";
        CloseChoice.Visibility = Visibility.Visible;
        BackgroundButton.Focus();
    }

    private void DismissCloseChoice()
    {
        CloseChoice.Visibility = Visibility.Collapsed;
        MainContent.IsEnabled = true;
        SettingsButton.Focus();
    }

    private void CancelClose_Click(object sender, RoutedEventArgs e) => DismissCloseChoice();

    private void CloseChoice_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; DismissCloseChoice(); }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => RequestExit();

    private void Background_Click(object sender, RoutedEventArgs e)
    {
        DismissCloseChoice();
        Hide();
    }

    private async void RequestExit()
    {
        if (exitRequested) return;
        exitRequested = true;
        topologyTimer.Stop();
        App.ShuttingDown = true;
        // Last resort: a shutdown that hangs on a wedged backend leaves a
        // process with no window that still holds the single-instance lock.
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(6)); Environment.Exit(0); });
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

    // ── session ────────────────────────────────────────────────────────────

    private async Task StartSessionAsync(
        ChainFiles files, IReadOnlyList<RadioDevice> devices, string selectedInstance,
        bool authorized, bool recoveryVerified,
        string python, string engineScript, string outputDirectory, string keysDirectory, string deviceName,
        UsbDeviceSnapshot? selectedTarget = null, bool allowLegacy = false)
    {
        if (session is not null) throw new InvalidOperationException("接收会话已经启动。");
        var created = new ReceiverSession();
        created.EngineEvent += message => OnEngineEvent(created, message);
        created.Failed += message => OnSessionFailed(created, message);
        created.Stage += message => OnSessionStage(created, message);
        created.Notice += message => OnSessionNotice(created, message);
        readySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        activeDevice = selectedInstance;
        session = created;
        try
        {
            await created.StartAsync(files, devices, selectedInstance, authorized, recoveryVerified,
                python, engineScript, outputDirectory, keysDirectory, deviceName, selectedTarget, allowLegacy);
            // A pause survives a restart: the user asked for quiet, and coming
            // back online un-paused would answer requests they declined.
            if (paused) await created.SetPausedAsync(true);
            _ = MonitorInitialReadinessAsync(created, readySignal.Task);
        }
        catch
        {
            await created.DisposeAsync();
            if (session == created) { session = null; SetReceiverReady(false); readySignal.TrySetResult(false); }
            throw;
        }
    }

    private async Task CloseSessionAsync()
    {
        var current = session;
        SetReceiverReady(false);
        readySignal.TrySetResult(false);
        session = null;
        if (current is not null) await current.DisposeAsync();
    }

    private async Task MonitorInitialReadinessAsync(ReceiverSession source, Task<bool> signal)
    {
        try { await signal.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            if (session == source && !receiverReady && !exitRequested)
                OnSessionFailed(source, "等待接收服务和发现广播就绪超时。");
        }
    }

    private void OnSessionFailed(ReceiverSession source, string message) =>
        _ = Dispatcher.BeginInvoke(async () =>
        {
            if (session != source || exitRequested) return;
            failure = "接收中断了，正在自动恢复";
            failureHint = message;
            AppLog.Write("session failed: " + message);
            try { await CloseSessionAsync(); }
            catch (Exception error) { AppLog.Write("close after failure: " + error.Message); }
            try { await CheckReadinessAsync(); }
            catch (Exception) { /* refresh is best-effort */ }
            Render();
            // No modal box: a receiver that needs a click to keep recovering is
            // a receiver that stays down while nobody is at the desk.

            ScheduleRestart(message);
        });

    private void ScheduleRestart(string reason)
    {
        if (exitRequested || restartScheduled || paused) return;
        restartAttempts++;
        if (restartAttempts > 6)
        {
            AppLog.Write("auto restart gave up: " + reason);
            failure = "自动恢复没能成功";
            failureHint = reason;
            Render();
            tray?.Notify("DropLink 已停止自动重试", "点开界面看看原因，或重新插一次网卡。");
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
            restartScheduled = false;
            if (exitRequested || paused || session is not null) return;
            await CheckReadinessAsync();
            if (exitRequested || paused || session is not null) return;
            if (currentStep != GuideStep.StartReceiving)
            {
                _ = WatchForDeviceAsync();
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

    // Watches for a card that comes back. It only runs while nothing is
    // receiving, so a healthy receiver never pays for it.
    private async Task WatchForDeviceAsync()
    {
        if (watchRunning || restartScheduled || starting) return;
        watchRunning = true;
        try
        {
            AppLog.Write("watching for a usable radio");
            // Device-change messages wake this immediately. The slower check
            // also covers drivers that omit a topology notification.
            var wait = TimeSpan.FromSeconds(10);
            while (!exitRequested && !paused && session is null)
            {
                await deviceChange.WaitAsync(wait);
                if (exitRequested || paused || session is not null) return;
                await CheckReadinessAsync();
                if (currentStep != GuideStep.StartReceiving)
                {
                    wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 2, 60));
                    continue;
                }
                if (!restartScheduled) await StartReceiveAsync(silent: true);
                return;
            }
        }
        catch (Exception error) { AppLog.Write("device watch stopped: " + error.Message); }
        finally { watchRunning = false; }
    }

    private IntPtr DeviceMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // DBT_DEVNODES_CHANGED is broadcast to top-level windows on a topology change.
        if (message == 0x0219 && wParam.ToInt64() == 7 && !exitRequested)
        {
            topologyTimer.Stop();
            topologyTimer.Start();
        }
        return IntPtr.Zero;
    }

    private async Task TopologyChangedAsync()
    {
        topologyTimer.Stop();
        if (checkingTopology) { topologyTimer.Start(); return; }
        if (exitRequested || paused) return;
        if (deviceChange.CurrentCount == 0) deviceChange.Release();
        var current = session;
        if (current is null)
        {
            if (!starting && !restartScheduled) _ = WatchForDeviceAsync();
            return;
        }
        if (activeDevice is null || starting) return;
        checkingTopology = true;
        try
        {
            var devices = await ReadinessProbe.DevicesAsync();
            if (current == session && !exitRequested && !devices.Any(d => ReadinessProbe.IsReadyCard(d)
                && string.Equals(d.InstanceId, activeDevice, StringComparison.OrdinalIgnoreCase)))
                OnSessionFailed(current, "接收网卡已断开。");
        }
        catch (Exception error) { AppLog.Write("USB change check: " + error.Message); }
        finally { checkingTopology = false; }
    }

    private bool watchRunning;

    private void OnSessionStage(ReceiverSession source, string message) =>
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (session != source || exitRequested) return;
            lastStage = message;
            if (transfers.Active is null && failure is null) Render();
        });

    private void OnSessionNotice(ReceiverSession source, string message) =>
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (session != source || exitRequested) return;
            AppLog.Write("notice: " + message);
            if (transfers.Active is not null) return;
            if (IsVisible) { failureHint = message; Render(); }

        });

    // ── engine events ──────────────────────────────────────────────────────

    private void OnEngineEvent(ReceiverSession source, JsonElement message) =>
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (session != source || exitRequested) return;
            if (!message.TryGetProperty("event", out var kindProperty)) return;
            var kind = kindProperty.GetString();

            var item = transfers.Apply(message);
            if (item is not null)
            {
                if (kind == "transfer_created" && item.RequiresConfirmation)
                {
                    confirmId = item.Id;
                    if (!IsVisible || WindowState == WindowState.Minimized)
                        tray?.Notify("有新的隔空投送", "点此选择接收或拒绝。");
                }
                if (item.IsFinished) confirmId = null;
                if (kind == "stored") RecordStored(message, item);
                transfers.Trim();
                Render();
                return;
            }

            switch (kind)
            {
                case "ready":
                    AppLog.Mark("engine:ready");
                    break;
                // 'receiving' is the moment an iPhone can see this PC, so it is
                // the state the header shows all day. Without it the line would
                // be a guess.
                case "receiving":
                    var live = message.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
                    SetReceiverReady(live);
                    if (live) currentStep = GuideStep.Receiving;
                    if (!live)
                    {
                        failure = "接收服务没有打开";
                        failureHint = "iPhone 看不到这台电脑时，多半是这里没起来。";
                    }
                    else if (failure is not null && session is not null && !paused)
                    {
                        failure = null;
                        failureHint = "";
                    }
                    Render();
                    break;
                case "settings":
                    if (message.TryGetProperty("refused", out var refused) && refused.ValueKind == JsonValueKind.String)
                    {
                        failureHint = "设置没有生效：" + refused.GetString();
                        Render();
                    }
                    break;
                case "error":
                    failureHint = message.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                        ? reason.GetString() ?? "" : "";
                    Render();
                    break;
            }
        });

    private void RecordStored(JsonElement message, TransferItem item)
    {
        try
        {
            var folder = message.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String
                ? f.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(folder)) folder = SettingsFile.ReceiveFolder();
            var names = new List<string>();
            if (message.TryGetProperty("names", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var name in array.EnumerateArray())
                    if (name.ValueKind == JsonValueKind.String) names.Add(name.GetString() ?? "");
            var batch = TransferHistory.From(item.Id, folder, names, item.Sender);
            history.Add(batch);
            UpdateRecentEmpty();
            ShowDone(batch.Files.Length == 0 ? "已保存" : $"已保存 {batch.Files.Length} 个文件");
        }
        catch (Exception error) { AppLog.Write("history record failed: " + error.Message); }
    }

    // A short, honest completion banner: it only appears after the files are on
    // disk, and it goes away by itself so the window returns to "等待接收".
    private void ShowDone(string text)
    {
        doneBanner = true;
        doneText = text;
        doneTimer?.Stop();
        doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        doneTimer.Tick += (_, _) =>
        {
            doneTimer?.Stop();
            doneBanner = false;
            Render();
        };
        doneTimer.Start();
        Render();
    }

    // What the × removed, so the empty line can offer to put it back. Null once
    // a new transfer lands: an undo that outlives new rows would silently drop
    // them when it is clicked.
    private List<HistoryBatch>? clearedHistory;

    private void UpdateRecentEmpty()
    {
        var empty = history.Recent.Count == 0;
        if (!empty) clearedHistory = null;
        RecentEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RecentEmpty.Text = clearedHistory is null
            ? "收到的文件会显示在这里"
            : "最近记录已清空，收到的文件还在";
        UndoClearButton.Visibility = clearedHistory is null ? Visibility.Collapsed : Visibility.Visible;
        ClearHistoryButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (history.Count == 0) return;
        var restore = history.Snapshot();
        ClearHistory();
        clearedHistory = restore;
        UpdateRecentEmpty();
    }

    private void UndoClear_Click(object sender, RoutedEventArgs e)
    {
        if (clearedHistory is null) return;
        var restore = clearedHistory;
        clearedHistory = null;
        history.Restore(restore);
        UpdateRecentEmpty();
        Render();
        AppLog.Write($"history undo: {restore.Count} record(s) restored");
    }

    // ── rendering: the single source of what the window says ───────────────

    private void Render()
    {
        var active = transfers.Active;
        var state = Resolve(active);
        IdlePanel.Visibility = Visibility.Collapsed;
        TransferPanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ActionPanel.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case UiState.Transferring:
                ApplyTransfer(active!);
                TransferPanel.Visibility = Visibility.Visible;
                break;
            case UiState.Confirming:
                ApplyConfirm(active!);
                ConfirmPanel.Visibility = Visibility.Visible;
                break;
            case UiState.Setup:
            case UiState.Paused:
            case UiState.NoDevice:
            case UiState.Failed:
                ApplyAction(state);
                ActionPanel.Visibility = Visibility.Visible;
                break;
            case UiState.Done:
                ApplyIdle("接收完成", doneText, deviceName, ready: true, caption: "接收服务已就绪");
                IdlePanel.Visibility = Visibility.Visible;
                break;
            case UiState.Preparing:
                ApplyIdle("正在准备接收…", "正在初始化无线网卡，通常 1 分钟", deviceName, ready: false, caption: "");
                IdlePanel.Visibility = Visibility.Visible;
                break;
            default:
                ApplyIdle("等待接收", "在 iPhone 的隔空投送中选择", deviceName, ready: true, caption: "接收服务已就绪");
                IdlePanel.Visibility = Visibility.Visible;
                break;
        }

        SetBusy(state == UiState.Preparing);
        SetBreathe(state == UiState.Preparing);
        SetDotPulse(state == UiState.Preparing);
        // While bytes are moving the panel is the progress block; keeping the
        // mark above it would put two focal points in the same area.
        MarkHost.Visibility = state == UiState.Transferring ? Visibility.Collapsed : Visibility.Visible;

        var headline = state switch
        {
            UiState.Transferring => active!.Headline,
            UiState.Confirming => "收到发送请求",
            UiState.Preparing => "正在准备接收",
            UiState.Done => "接收完成",
            UiState.Paused => "接收已暂停",
            UiState.NoDevice => "请连接无线网卡",
            UiState.Failed => "暂时无法接收",
            UiState.Setup => "需要完成一次配置",
            _ => "等待接收"
        };
        Title = "DropLink · " + headline;
        tray?.SetStatus(headline);
        tray?.SetPaused(paused);
    }

    private UiState Resolve(TransferItem? active)
    {
        if (active is not null) return active.Stage == TransferStage.Awaiting ? UiState.Confirming : UiState.Transferring;
        if (paused) return UiState.Paused;
        if (failure is not null) return UiState.Failed;
        if (doneBanner && receiverReady) return UiState.Done;
        if (supportedCards == 0 && guide is not null) return UiState.NoDevice;
        if (currentStep is GuideStep.SwitchDriver or GuideStep.Rescan or GuideStep.EnvironmentBlocked or GuideStep.NeedRestart)
            return UiState.Setup;
        if (!receiverReady || starting || currentStep is GuideStep.Starting or GuideStep.Checking) return UiState.Preparing;
        return UiState.Waiting;
    }

    private void ApplyIdle(string title, string line, string device, bool ready, string caption)
    {
        IdleTitle.Text = title;
        IdleLine.Text = line;
        IdleLine.Visibility = string.IsNullOrWhiteSpace(line) ? Visibility.Collapsed : Visibility.Visible;
        IdleDevice.Text = device;
        IdleDevice.Visibility = string.IsNullOrWhiteSpace(device) ? Visibility.Collapsed : Visibility.Visible;
        ReadyLine.Text = caption;
        ReadyDot.Visibility = string.IsNullOrEmpty(caption) ? Visibility.Collapsed : Visibility.Visible;
        ReadyLine.Visibility = string.IsNullOrEmpty(caption) ? Visibility.Collapsed : Visibility.Visible;
        ReadyDot.Fill = ready ? (Brush)FindResource("Success") : (Brush)FindResource("Warning");
    }

    private void ApplyTransfer(TransferItem item)
    {
        TransferHeadline.Text = item.Headline;
        TransferFrom.Text = $"{item.Sender} · {item.CountText}";
        TransferPercent.Text = item.HasPercent ? item.PercentText : "";
        TransferPercent.Visibility = item.HasPercent ? Visibility.Visible : Visibility.Collapsed;
        TransferBar.Value = item.Fraction * 100;
        TransferBar.Visibility = item.HasPercent ? Visibility.Visible : Visibility.Collapsed;
        TransferReceived.Text = item.ReceivedText;
        // Left: how much of how much. Right: how long. The speed goes on the
        // note line, because "还剩 12 秒" is the number people actually watch.
        TransferRemaining.Text = item.Stage == TransferStage.Saving ? "" : item.RemainingText;
        TransferRemaining.Visibility = string.IsNullOrWhiteSpace(TransferRemaining.Text) ? Visibility.Collapsed : Visibility.Visible;
        TransferNote.Text = item.Stage == TransferStage.Saving
            ? "上传已完成，正在解包并保存到磁盘"
            : item.SpeedText;
        TransferNote.Visibility = string.IsNullOrWhiteSpace(TransferNote.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyConfirm(TransferItem item)
    {
        confirmId = item.Id;
        ConfirmFrom.Text = $"{item.Sender} 想发送 {item.CountText}";
        ConfirmFiles.Text = item.Names.Length == 0 ? "" : string.Join("、", item.Names.Take(4)) +
            (item.Names.Length > 4 ? $" 等 {item.Names.Length} 个" : "");
        ConfirmFiles.Visibility = string.IsNullOrWhiteSpace(ConfirmFiles.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyAction(UiState state)
    {
        ActionSecondary.Visibility = Visibility.Collapsed;
        switch (state)
        {
            case UiState.Setup:
                ActionTitle.Text = guide?.Title ?? "配置一次，开始隔空投送";
                ActionDetail.Text = guide?.Detail ?? "";
                ActionButton.Content = string.IsNullOrWhiteSpace(guide?.PrimaryAction) ? "重新检查" : guide.PrimaryAction;
                break;
            case UiState.Paused:
                ActionTitle.Text = "接收已暂停";
                ActionDetail.Text = "新的请求会被拒绝，在传的会做完。";
                ActionButton.Content = "恢复接收";
                break;
            case UiState.NoDevice:
                ActionTitle.Text = "请连接无线网卡";
                ActionDetail.Text = "插上网卡会自动恢复，不用重开。";
                ActionButton.Content = "重新检查";
                break;
            default:
                ActionTitle.Text = "暂时无法接收";
                ActionDetail.Text = string.IsNullOrWhiteSpace(failureHint) ? failure ?? "" : failureHint;
                ActionButton.Content = "重试";
                break;
        }
    }

    private void SetBusy(bool on)
    {
        Spinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MarkGroup.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on == spinnerOn) return;
        spinnerOn = on;
        if (on) ((System.Windows.Media.Animation.Storyboard)FindResource("Spin")).Begin(this, true);
        else SpinAngle.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void SetBreathe(bool on)
    {
        if (on == breatheOn) return;
        breatheOn = on;
        if (on) ((System.Windows.Media.Animation.Storyboard)FindResource("Breathe")).Begin(this, true);
        else
        {
            ((System.Windows.Media.Animation.Storyboard)FindResource("Breathe")).Stop(this);
            MarkGroup.Opacity = 1;
        }
    }

    private void SetDotPulse(bool on)
    {
        if (on == dotPulse) return;
        dotPulse = on;
        if (on) ((System.Windows.Media.Animation.Storyboard)FindResource("PulseDot")).Begin(this, true);
        else
        {
            ((System.Windows.Media.Animation.Storyboard)FindResource("PulseDot")).Stop(this);
            ReadyDot.Opacity = 1;
        }
    }

    // ── user actions ───────────────────────────────────────────────────────

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        var id = confirmId;
        if (id is null) return;
        try { if (session is not null) await session.AnswerAsync(id, true); }
        catch (Exception error) { AppLog.Write("answer failed: " + error.Message); }
        confirmId = null;
        Render();
    }

    private async void Decline_Click(object sender, RoutedEventArgs e)
    {
        var id = confirmId;
        if (id is null) return;
        try { if (session is not null) await session.AnswerAsync(id, false); }
        catch (Exception error) { AppLog.Write("answer failed: " + error.Message); }
        confirmId = null;
        Render();
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (paused) { ResumeReceiving(); return; }
        switch (currentStep)
        {
            case GuideStep.SwitchDriver:
            case GuideStep.Rescan:
            case GuideStep.NeedRestart:
            case GuideStep.EnvironmentBlocked:
                OpenSetup();
                return;
            case GuideStep.StartReceiving:
                await StartReceiveAsync(silent: false);
                return;
            default:
                failure = null;
                failureHint = "";
                await CheckReadinessAsync();
                if (currentStep == GuideStep.StartReceiving && session is null && !paused)
                    await StartReceiveAsync(silent: false);
                else if (session is null && !paused) _ = WatchForDeviceAsync();
                return;
        }
    }

    private void ActionSecondary_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    /// <summary>
    /// The first-run flow. It is a window of its own because "配置一次就能用"
    /// is a four-screen promise, and squeezing it into one status line is what
    /// made the old build look like it was hiding something.
    /// </summary>
    private void OpenSetup()
    {
        if (setupWindow is not null)
        {
            setupWindow.Activate();
            return;
        }
        var window = new SetupWindow(VerifyReceivingAsync) { Owner = this };
        setupWindow = window;
        try { window.ShowDialog(); }
        finally
        {
            setupWindow = null;
            _ = CheckReadinessAsync();
        }
    }

    /// <summary>
    /// Step 4 of the setup flow: bring the receiver up for real and report what
    /// actually happened. Returning "ok" here is only possible when a session
    /// exists, so the wizard cannot claim success on its own.
    /// </summary>
    internal async Task<(bool Ok, string Message)> VerifyReceivingAsync()
    {
        if (session is null)
        {
            await CheckReadinessAsync();
            if (currentStep == GuideStep.StartReceiving) await StartReceiveAsync(silent: false);
        }
        if (session is not null && !receiverReady)
        {
            try { await readySignal.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { return (false, "尚未收到接收服务的就绪确认。"); }
        }
        if (receiverReady)
            return (true, "接收服务已就绪，请在 iPhone 的隔空投送里选择这台电脑。");
        var reason = !string.IsNullOrWhiteSpace(failureHint) ? failureHint
            : !string.IsNullOrWhiteSpace(failure) ? failure
            : guide?.Detail ?? "";
        return (false, reason);
    }

    private void HistoryRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HistoryBatch batch) return;
        if (batch.Missing)
        {
            var answer = MessageBox.Show(this,
                "这条记录里的文件已经不在了（可能被移动或删除）。\n\n要从列表里移除这条记录吗？",
                "文件不存在", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Yes)
            {
                history.Remove(batch);
                UpdateRecentEmpty();
            }
            return;
        }
        try
        {
            if (batch.Files.Length == 1 && File.Exists(batch.Target))
                Process.Start(new ProcessStartInfo(batch.Target) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + Path.Combine(batch.Folder, batch.Files.FirstOrDefault() ?? "") + "\"")
                { UseShellExecute = true });
        }
        catch (Exception error) { AppLog.Write("open history row failed: " + error.Message); }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { await SettingsFile.OpenReceiveFolderAsync(); }
        catch (Exception error) { AppLog.Write("open folder failed: " + error.Message); }
    }

    internal void TogglePause()
    {
        if (paused) ResumeReceiving();
        else PauseReceiving();
    }

    internal void PauseReceiving()
    {
        paused = true;
        failure = null;
        failureHint = "";
        AppLog.Write("receiving paused by user");
        _ = session?.SetPausedAsync(true);
        Render();
    }

    internal void ResumeReceiving()
    {
        paused = false;
        AppLog.Write("receiving resumed by user");
        Render();
        if (session is not null) { _ = session.SetPausedAsync(false); return; }
        _ = StartReceiveAsync(silent: true);
    }

    // ── readiness ──────────────────────────────────────────────────────────

    private async Task CheckReadinessAsync()
    {
        try
        {
            var snapshot = await ReadinessProbe.TakeAsync();
            readyCards = snapshot.ReadyWinUsbCards;
            supportedCards = snapshot.SupportedCards;

            var result = SetupGuide.Evaluate(snapshot.ToGuideInput(receiverReady, starting || session is not null));
            currentStep = result.Step;
            guide = result;
            if (currentStep is GuideStep.StartReceiving or GuideStep.Starting or GuideStep.Receiving && failure is not null && receiverReady)
            {
                failure = null;
                failureHint = "";
            }
            Render();
        }
        catch (Exception ex)
        {
            AppLog.Write("readiness check failed: " + ex.Message);
            failure = "自动检查没有完成";
            failureHint = ex.Message;
            Render();
        }
    }

    private async Task StartReceiveAsync(bool silent)
    {
        if (exitRequested || paused) return;
        if (session is not null || starting) return;
        starting = true;
        failure = null;
        failureHint = "";
        Render();
        AppLog.Mark("session:start requested");
        try
        {
            var snapshot = await ReadinessProbe.TakeAsync();
            var devices = snapshot.Devices;
            var (pnp, selectedTarget, legacy) = (snapshot.Pnp, snapshot.Target, snapshot.Legacy);
            if (pnp is null)
                throw new InvalidOperationException("还没定位到网卡，先完成一次配置。");
            var files = new ChainFiles(
                Environment.GetEnvironmentVariable("DROPLINK_RADIO_EXE") ?? "",
                Environment.GetEnvironmentVariable("DROPLINK_BRIDGE_EXE") ?? "",
                Environment.GetEnvironmentVariable("DROPLINK_NETD_EXE") ?? "",
                Environment.GetEnvironmentVariable("DROPLINK_AWDLD_EXE") ?? "");
            await StartSessionAsync(files, devices, pnp.InstanceId,
                ReadinessProbe.EnvFlag("DROPLINK_AUTHORIZED"), ReadinessProbe.EnvFlag("DROPLINK_RECOVERY_VERIFIED"),
                Environment.GetEnvironmentVariable("DROPLINK_PYTHON") ?? "python",
                Environment.GetEnvironmentVariable("DROPLINK_ENGINE_SCRIPT") ?? "",
                SettingsFile.ReceiveFolder(),
                Environment.GetEnvironmentVariable("DROPLINK_KEYS_DIR") ?? "",
                deviceName, selectedTarget, legacy);
            await CheckReadinessAsync();
        }
        catch (Exception ex)
        {
            if (exitRequested) return;
            // The chain came up; only the follow-up refresh failed. Printing a
            // failure here would be a lie the user can see through.
            if (session is not null)
            {
                AppLog.Write("post-start refresh failed: " + ex.Message);
                Render();
                return;
            }
            var stage = string.IsNullOrWhiteSpace(lastStage) ? "启动准备" : lastStage.TrimEnd('…', '.', ' ');
            AppLog.Write($"start failed at [{stage}]: {ex.Message}");
            failure = "接收没能启动（" + stage + "）";
            failureHint = ex.Message;
            Render();

            ScheduleRestart(ex.Message);
        }
        finally
        {
            starting = false;
            Render();
        }
    }

    // ── dev preview ────────────────────────────────────────────────────────

    /// <summary>
    /// Forces one state for `--preview-main out.png`. The numbers below are
    /// sample data for a layout render only; a real run never reaches this
    /// method, and nothing here can make the live window claim a transfer that
    /// did not happen.
    /// </summary>
    internal void PreviewState(string state)
    {
        supportedCards = 1;
        readyCards = 1;
        receiverReady = true;
        currentStep = GuideStep.Receiving;
        // Screenshots are committed to the repository, so the machine's own name
        // must never reach them. It used to, because the preview rendered
        // whatever Environment.MachineName returned on the build machine.
        deviceName = "我的电脑";
        guide = new SetupGuideResult(GuideStep.Receiving, "等待接收", "", "", false);
        transfers.Items.Clear();
        paused = false;
        failure = null;
        failureHint = "";
        starting = false;
        doneBanner = false;
        switch (state)
        {
            case "preparing":
                currentStep = GuideStep.Starting;
                starting = true;
                break;
            case "setup":
                currentStep = GuideStep.SwitchDriver;
                // Mirrors SetupGuide's first-run copy. It is sample text for a
                // layout render; the live window always gets it from the guide.
                guide = new SetupGuideResult(GuideStep.SwitchDriver,
                    "配置一次，开始隔空投送",
                    "需要把这张无线网卡切换成 WinUSB。一次即可，随时可以回退。",
                    "开始配置", true);
                break;
            case "nodevice":
                supportedCards = 0;
                break;
            case "paused":
                paused = true;
                break;
            case "failed":
                failure = "接收没能启动（初始化网卡）";
                failureHint = "网卡没有响应，已经自动重试 3 次。";
                break;
            case "done":
                doneBanner = true;
                doneText = "已保存 3 个文件";
                break;
            case "transfer":
                var live = new TransferItem
                {
                    Id = "preview-live", Sender = "iPhone", FileCount = 12,
                    Stage = TransferStage.Receiving
                };
                live.TotalBytes = 1_932_735_283;
                live.DoneBytes = 1_310_183_424;
                live.BytesPerSecond = 54_000_000;
                transfers.Items.Add(live);
                break;
            case "confirm":
                var ask = new TransferItem
                {
                    Id = "preview-ask", Sender = "iPhone", FileCount = 3,
                    Names = ["IMG_2841.HEIC", "IMG_2840.MOV", "IMG_2839.HEIC"],
                    RequiresConfirmation = true, Stage = TransferStage.Awaiting
                };
                ask.TotalBytes = 137_363_456;
                transfers.Items.Add(ask);
                break;
        }
        if (state is "waiting" or "transfer" or "done" or "close")
        {
            history.Recent.Clear();
            history.Recent.Add(new HistoryBatch { Id = "p1", At = DateTime.Now.AddSeconds(-20), Folder = SettingsFile.ReceiveFolder(), Files = ["旅行照片_2026_海边日落_IMG_2841_原始影像_全分辨率.HEIC"], Bytes = 9_017_344, Kind = "image", Sender = "iPhone" });
            history.Recent.Add(new HistoryBatch { Id = "p2", At = DateTime.Now.AddMinutes(-12), Folder = SettingsFile.ReceiveFolder(), Files = ["IMG_2840.MOV"], Bytes = 134_637_568, Kind = "video", Sender = "iPhone" });
            history.Recent.Add(new HistoryBatch { Id = "p3", At = DateTime.Now.AddHours(-3), Folder = SettingsFile.ReceiveFolder(), Files = ["a.heic", "b.heic", "c.heic", "d.heic", "e.heic", "f.heic"], Bytes = 44_875_776, Kind = "image", Sender = "iPhone" });
            UpdateRecentEmpty();
        }
        else if (state == "cleared")
        {
            // The state right after the × is pressed, before the undo expires.
            history.Recent.Clear();
            clearedHistory = [new HistoryBatch { Id = "p1", At = DateTime.Now, Folder = SettingsFile.ReceiveFolder(), Files = ["IMG_2841.HEIC"], Bytes = 9_017_344, Kind = "image", Sender = "iPhone" }];
            UpdateRecentEmpty();
        }
        Render();
        if (state == "close") ShowCloseChoice();
    }

}
