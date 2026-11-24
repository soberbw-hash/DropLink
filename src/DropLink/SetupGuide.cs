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
                "后端组件缺失",
                "把 backend 文件夹放回软件目录，再重新打开。",
                "重新检查", true);

        if (!input.Authorized || !input.RecoveryVerified)
            return new SetupGuideResult(
                GuideStep.NeedRestart, "准备阶段",
                "需要重新打开",
                "托盘右键退出 DropLink，再双击启动脚本打开。",
                "重新检查", true);

        if (input.SessionRunning)
            return new SetupGuideResult(
                GuideStep.Receiving, "第 3 步 / 共 3 步",
                "正在接收",
                "在 iPhone 上用隔空投送分享文件，选这台电脑。",
                "", false);

        if (input.ReadyWinUsbCards == 0)
            return new SetupGuideResult(
                GuideStep.SwitchDriver, "第 1 步 / 共 3 步",
                "先把网卡换成 WinUSB",
                "一次即可，随时可回退。",
                "一键换 WinUSB", true);

        if (!input.HasLibusbTarget)
            return new SetupGuideResult(
                GuideStep.Rescan, "第 1 步 / 共 3 步",
                "还差最后一步",
                "把网卡拔下重插，再点检查。",
                "重新检查", true);

        return new SetupGuideResult(
            GuideStep.StartReceiving, "第 2 步 / 共 3 步",
            "可以开始接收了",
            "点按钮，然后在 iPhone 上发送。",
            "开启接收", true);
    }
}
