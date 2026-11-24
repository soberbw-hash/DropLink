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
    Starting,           // step 2 in progress: the chain is being brought up
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
        bool SessionRunning,
        bool Starting);

    internal static SetupGuideResult Evaluate(Input input)
    {
        // Environment first: the four backend exe files must exist on disk.
        // They are NOT in the GitHub repo - they live in work/prepared-next next
        // to the DropLink folder. A fresh clone on another PC will not have them.
        var files = input.RequiredFiles;
        if (files.Count < 4 || files.Any(string.IsNullOrWhiteSpace) || files.Any(f => !File.Exists(f)))
            return new SetupGuideResult(
                GuideStep.EnvironmentBlocked, "准备阶段",
                "软件文件不完整",
                "backend 文件夹没有放在软件目录里，补全后再打开一次。",
                "重新检查", true);

        if (!input.Authorized || !input.RecoveryVerified)
            return new SetupGuideResult(
                GuideStep.NeedRestart, "准备阶段",
                "需要重新打开一次",
                "点右上角 × 退出 DropLink，再重新打开。",
                "重新检查", true);

        if (input.SessionRunning)
            return new SetupGuideResult(
                GuideStep.Receiving, "第 3 步 / 共 3 步",
                "正在接收",
                "在 iPhone 上用隔空投送分享文件，选这台电脑。",
                "", false);

        // A chain that is still coming up is not "ready to start": saying so
        // would put a button under a status line that already says it is
        // working, and pressing it started a second chain on top of the first.
        if (input.Starting)
            return new SetupGuideResult(
                GuideStep.Starting, "第 2 步 / 共 3 步",
                "正在初始化网卡",
                "通常 1 分钟，偶尔要几分钟。完成后会自动开始接收，这期间不用点任何东西。",
                "", false);

        if (input.ReadyWinUsbCards == 0)
            return new SetupGuideResult(
                GuideStep.SwitchDriver, "第 1 步 / 共 3 步",
                "完成一次配置，即可开始隔空投送",
                "需要把这张无线网卡切换成 WinUSB。一次即可，随时可以回退。",
                "开始配置", true);

        if (!input.HasLibusbTarget)
            return new SetupGuideResult(
                GuideStep.Rescan, "第 1 步 / 共 3 步",
                "还差最后一步",
                "把网卡拔下再插一次，让它重新被识别。",
                "重新检查", true);

        return new SetupGuideResult(
            GuideStep.StartReceiving, "第 2 步 / 共 3 步",
            "正在准备接收",
            "条件已满足，正在自动开始，这期间不用点任何东西。",
            "开启接收", true);
    }
}
