using System.IO;

namespace DropLink;

// Where the user is in the one-time setup -> daily use flow. Pure decision
// logic over read-only snapshots. It never opens a device, changes a driver,
// starts a process or sends frames; it only decides what the UI should say.
internal enum GuideStep
{
    EnvironmentBlocked, // backend files missing / not launched via start script
    NeedRestart,        // authorization / recovery flags not set
    SwitchDriver,       // step 1: no supported card bound to WinUSB yet
    Rescan,             // WinUSB present but not yet pinned to a libusb device
    StartReceiving,     // step 2: all preconditions met
    Receiving           // step 3: session running, waiting for an iPhone
}

internal sealed record SetupGuideResult(
    GuideStep Step,
    string StepLabel,
    string Title,
    string Detail,
    string PrimaryAction,
    bool PrimaryActionVisible);

internal static class SetupGuide
{
    // All values come from the same read-only facts the real chain checks, so
    // what the guide says can never be more permissive than BackendChain.
    internal readonly record struct Input(
        IReadOnlyList<string> RequiredFiles,
        int ReadyWinUsbCards,
        bool HasLibusbTarget,
        bool Authorized,
        bool RecoveryVerified,
        bool SessionRunning);

    internal static SetupGuideResult Evaluate(Input input)
    {
        // Environment first: the four backend exe files must exist on disk.
        // They are NOT in the GitHub repo - they live in work/prepared-next next
        // to the DropLink folder. A fresh clone on another PC will not have them.
        var files = input.RequiredFiles;
        if (files.Count < 4 || files.Any(string.IsNullOrWhiteSpace) || files.Any(f => !File.Exists(f)))
            return new SetupGuideResult(
                GuideStep.EnvironmentBlocked, "准备阶段",
                "后端组件缺失（换电脑没拷全）",
                "软件需要 4 个后端程序才能工作，它们不在 GitHub 仓库里。如果你是换电脑或新克隆，请把原电脑 work 文件夹下的 prepared-next 和 _backup 两个文件夹整个拷过来，和 DropLink 保持同级（目录结构和原来一模一样），再重新打开本软件。点启动脚本无法生成这些文件。",
                "重新检查", true);

        if (!input.Authorized || !input.RecoveryVerified)
            return new SetupGuideResult(
                GuideStep.NeedRestart, "准备阶段",
                "需要重新打开本软件",
                "授权状态与驱动回退确认没有生效。关掉本窗口，重新打开「启动软件.cmd」或「启动软件.vbs」即可。",
                "重新检查", true);

        if (input.SessionRunning)
            return new SetupGuideResult(
                GuideStep.Receiving, "第 3 步 / 共 3 步",
                "正在接收，等 iPhone 发送",
                "在 iPhone 上打开 控制中心 → 隔空投送 → 所有人（10 分钟），然后分享照片或文件，选择这台电脑。电脑弹出确认框时点「是」即可收到。",
                "", false);

        if (input.ReadyWinUsbCards == 0)
            return new SetupGuideResult(
                GuideStep.SwitchDriver, "第 1 步 / 共 3 步",
                "先把网卡换成 WinUSB",
                "你的 RTL8811CU 网卡现在是普通 Wi-Fi 驱动，收不了隔空投送。点下面的绿色按钮，按提示输入 SWITCH，一次即可，随时能一键回退。",
                "一键换 WinUSB", true);

        if (!input.HasLibusbTarget)
            return new SetupGuideResult(
                GuideStep.Rescan, "第 1 步 / 共 3 步",
                "驱动换好了，还差最后确认",
                "检测到网卡已是 WinUSB，但系统还没在底层定位到它。把网卡拔下重插一次，然后点下面的「检查接收条件」。",
                "检查接收条件", true);

        return new SetupGuideResult(
            GuideStep.StartReceiving, "第 2 步 / 共 3 步",
            "条件齐全，可以开始接收了",
            "点下面的「开启接收」启动接收链路，然后在 iPhone 的隔空投送里选这台电脑发送。",
            "开启接收", true);
    }
}
