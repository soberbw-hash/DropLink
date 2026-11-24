using System.IO;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace DropLink;

/// <summary>
/// The one-time setup, as four screens instead of a single line of text that
/// tells the user to go and do something else.
///
/// Every screen reports what was actually found on this machine. Nothing here
/// pretends to be further along than the real chain: step 1 can only be passed
/// when a supported card is bound to WinUSB, and the last step only completes
/// when the receiver is really listening.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly Func<Task<(bool Ok, string Message)>> verify;
    private int step = 1;
    private ReadinessSnapshot? snapshot;
    private bool busy;
    private bool finished;

    internal SetupWindow(Func<Task<(bool Ok, string Message)>> verify)
    {
        this.verify = verify;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    /// <summary>Dev-only: forces one screen for `--preview-setup`. The facts on
    /// it still come from a real probe of this machine, so the screenshot can
    /// never show a state the code cannot produce.</summary>
    internal Task PreviewAsync(int value)
    {
        step = Math.Clamp(value, 1, 4);
        return RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try { snapshot = await ReadinessProbe.TakeAsync(); }
        catch (Exception error)
        {
            AppLog.Write("setup probe failed: " + error.Message);
            snapshot = null;
            NoteText.Text = "没有读到网卡状态：" + error.Message;
        }
        Render();
    }

    // ── what each screen says ──────────────────────────────────────────────

    private void Render()
    {
        var dots = new[] { Dot1, Dot2, Dot3, Dot4 };
        for (var i = 0; i < dots.Length; i++)
            dots[i].Fill = (Brush)FindResource(i + 1 == step ? "Brand" : "Divider");
        StepCount.Text = $"第 {step} 步 / 共 4 步";

        FactsCard.Visibility = Visibility.Collapsed;
        PreferenceCard.Visibility = Visibility.Collapsed;
        BackButton.Visibility = step > 1 && !finished ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = !busy;

        switch (step)
        {
            case 1: RenderCard(); break;
            case 2: RenderEnvironment(); break;
            case 3: RenderPreferences(); break;
            default: RenderVerify(); break;
        }
    }

    private void RenderCard()
    {
        if (snapshot is null)
        {
            StepTitle.Text = "没有读到网卡状态";
            StepDetail.Text = "再检查一次试试。如果一直是这样，可能是系统查询被拦截了。";
            NextButton.Content = "重新检查";
            return;
        }

        var files = snapshot.RequiredFiles;
        if (files.Count < 4 || files.Any(string.IsNullOrWhiteSpace) || files.Any(f => !File.Exists(f)))
        {
            StepTitle.Text = "软件文件不完整";
            StepDetail.Text = "backend 文件夹没有放在软件目录里。补全之后再打开一次 DropLink。";
            NextButton.Content = "重新检查";
            return;
        }

        if (snapshot.SupportedCards == 0)
        {
            StepTitle.Text = "没有找到可用的无线网卡";
            StepDetail.Text = "DropLink 需要一张受支持的无线网卡来收发隔空投送。插上之后点下面的按钮重新检查。";
            ShowFacts("当前没有检测到受支持的网卡", "支持 RTL8811CU / RTL8821CU 这一类的 USB 无线网卡。");
            NextButton.Content = "重新检查";
            return;
        }

        if (snapshot.ReadyWinUsbCards == 0)
        {
            StepTitle.Text = "把无线网卡切换成 WinUSB";
            StepDetail.Text = "这一步只做一次。切换之后这张网卡不能再当 Wi-Fi 用，随时可以在设置里回退。大约 10~60 秒。";
            ShowFacts($"检测到 {snapshot.SupportedCards} 张受支持的网卡，还没有一张就绪",
                "切换过程中网卡会重新枚举，屏幕右下角可能弹一次硬件提示，正常。");
            NextButton.Content = "切换网卡驱动";
            return;
        }

        StepTitle.Text = "网卡已就绪";
        StepDetail.Text = snapshot.HasTarget
            ? "已经找到这张网卡，可以继续下一步。"
            : "网卡已经切换好了，但还没和接收程序对上。把网卡拔下再插一次，然后点重新检查。";
        ShowFacts($"{snapshot.ReadyWinUsbCards} 张网卡已就绪", snapshot.HasTarget ? "接收程序能定位到它。" : "还差一次重新识别。");
        NextButton.Content = snapshot.HasTarget ? "继续" : "重新检查";
    }

    private void RenderEnvironment()
    {
        var files = snapshot?.RequiredFiles ?? [];
        var missing = files.Count(f => string.IsNullOrWhiteSpace(f) || !File.Exists(f));
        var authorized = snapshot?.Authorized == true;
        var recovery = snapshot?.RecoveryVerified == true;

        if (missing > 0)
        {
            StepTitle.Text = "接收程序文件不齐";
            StepDetail.Text = "缺了 " + missing + " 个后台程序，接收起不来。把 DropLink 文件夹整个重新放一次。";
            NextButton.Content = "重新检查";
            return;
        }
        if (!authorized || !recovery)
        {
            StepTitle.Text = "需要重新打开一次";
            StepDetail.Text = "权限标记还没写进这个进程。关掉 DropLink 再打开一次就好。";
            NextButton.Content = "重新检查";
            return;
        }

        StepTitle.Text = "接收环境已就绪";
        StepDetail.Text = "后台程序、权限和保存目录都检查过了，没有缺项。";
        ShowFacts("4 个后台程序齐全", "权限和恢复标记都已确认。");
        NextButton.Content = "继续";
    }

    private void RenderPreferences()
    {
        StepTitle.Text = "接收偏好";
        StepDetail.Text = "这三项随时可以在设置里改，现在只是先定一个默认值。";
        PreferenceCard.Visibility = Visibility.Visible;
        AutoReceiveSwitch.IsChecked = SettingsFile.Flag("auto_receive", true);
        NameBox.Text = SettingsFile.Text("device_name");
        FolderText.Text = SettingsFile.ReceiveFolder();
        NextButton.Content = "保存并继续";
    }

    private void RenderVerify()
    {
        PreferenceCard.Visibility = Visibility.Collapsed;
        if (finished)
        {
            StepTitle.Text = "可以开始用了";
            StepDetail.Text = "在 iPhone 的隔空投送里选这台电脑就行。以后打开 DropLink 它会自己在后台等着，不用再点任何东西。";
            NextButton.Content = "完成";
            BackButton.Visibility = Visibility.Collapsed;
            return;
        }
        StepTitle.Text = "验证一下";
        StepDetail.Text = "下面会让接收服务真的起来一次，确认这台电脑能被 iPhone 找到。";
        ShowFacts("这一步不传文件",
            "只是把接收服务启动起来，看它能不能正常监听。",
            "如果起不来，这里会直接写出原因，不会只显示一个失败。");
        NextButton.Content = "开始接收";
    }

    private void ShowFacts(string line1, string line2, string line3 = "")
    {
        FactsCard.Visibility = Visibility.Visible;
        FactLine1.Text = line1;
        FactLine2.Text = line2;
        FactLine3.Text = line3;
        FactLine3.Visibility = string.IsNullOrWhiteSpace(line3) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── actions ────────────────────────────────────────────────────────────

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        NoteText.Text = "";
        switch (step)
        {
            case 1: await AdvanceCardAsync(); break;
            case 2: await AdvanceEnvironmentAsync(); break;
            case 3: AdvancePreferences(); break;
            default: await VerifyAsync(); break;
        }
    }

    private async Task AdvanceCardAsync()
    {
        if (snapshot is null || snapshot.SupportedCards == 0 || !snapshot.HasTarget)
        {
            if (snapshot is not null && snapshot.SupportedCards > 0 && snapshot.ReadyWinUsbCards == 0)
            {
                await SwitchDriverAsync();
                return;
            }
            await RefreshAsync();
            return;
        }
        step = 2;
        Render();
    }

    private async Task SwitchDriverAsync()
    {
        var script = DriverSwitch.ScriptFor(true);
        if (script is null)
        {
            NoteText.Text = "找不到换驱动脚本，这个安装包可能不完整。";
            return;
        }
        if (MessageBox.Show(this, DriverSwitch.Question(true), "配置网卡",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        busy = true;
        NextButton.IsEnabled = false;
        NextButton.Content = "正在切换…";
        NoteText.Text = "网卡会断开重连，这期间不用管。";
        try
        {
            var result = await DriverSwitch.RunAsync(true);
            if (!result.Ok)
            {
                AppLog.Write("setup driver switch failed: " + result.Detail);
                NoteText.Text = "没有切换成功。可以到设置 → 设备维护里看详细步骤。";
            }
        }
        catch (Exception error)
        {
            AppLog.Write("setup driver switch crashed: " + error.Message);
            NoteText.Text = "切换失败：" + error.Message;
        }
        finally
        {
            busy = false;
        }
        await RefreshAsync();
    }

    private async Task AdvanceEnvironmentAsync()
    {
        var files = snapshot?.RequiredFiles ?? [];
        var ok = files.Count == 4 && files.All(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f))
                 && snapshot is { Authorized: true, RecoveryVerified: true };
        if (!ok)
        {
            await RefreshAsync();
            return;
        }
        step = 3;
        Render();
    }

    private void AdvancePreferences()
    {
        try
        {
            var name = NameBox.Text.Trim();
            if (name.Length > 0 && name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                NoteText.Text = "这个名字用不了：不能包含 / \\ : * ? \" < > |。";
                return;
            }
            var settings = SettingsFile.Read();
            settings["auto_receive"] = AutoReceiveSwitch.IsChecked == true;
            if (name.Length > 0) settings["device_name"] = name;
            else settings.Remove("device_name");
            SettingsFile.Save(settings);
        }
        catch (Exception error)
        {
            NoteText.Text = "保存失败：" + error.Message;
            return;
        }
        step = 4;
        Render();
    }

    private async Task VerifyAsync()
    {
        if (finished) { Close(); return; }
        busy = true;
        NextButton.IsEnabled = false;
        NextButton.Content = "正在启动…";
        NoteText.Text = "第一次启动要初始化网卡，通常 1 分钟。";
        try
        {
            var (ok, message) = await verify();
            finished = ok;
            if (ok)
            {
                NoteText.Text = message;
            }
            else
            {
                NoteText.Text = string.IsNullOrWhiteSpace(message) ? "接收服务没有起来。" : message;
                NextButton.Content = "重试";
            }
        }
        catch (Exception error)
        {
            AppLog.Write("setup verify failed: " + error.Message);
            NoteText.Text = "启动失败：" + error.Message;
            NextButton.Content = "重试";
        }
        finally
        {
            busy = false;
            NextButton.IsEnabled = true;
        }
        Render();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (busy || finished || step <= 1) return;
        step--;
        NoteText.Text = "";
        Render();
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择接收文件的保存位置",
            InitialDirectory = SettingsFile.ReceiveFolder()
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SettingsFile.Set("receive_folder", dialog.FolderName);
            FolderText.Text = SettingsFile.ReceiveFolder();
        }
        catch (Exception error)
        {
            NoteText.Text = "保存位置没有改成：" + error.Message;
        }
    }
}
