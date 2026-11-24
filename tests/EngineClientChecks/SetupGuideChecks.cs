using DropLink;

internal static class SetupGuideChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static string[] FourFiles(string temp)
    {
        var result = new string[4];
        for (var i = 0; i < 4; i++)
        {
            var path = Path.Combine(temp, "component" + i + ".exe");
            File.WriteAllText(path, "x");
            result[i] = path;
        }
        return result;
    }

    internal static void Run(string temp)
    {
        var files = FourFiles(temp);

        // Missing backend -> environment blocked, tells user to COPY the folders
        // (not to keep clicking the start script, which cannot create the files).
        var env = SetupGuide.Evaluate(new SetupGuide.Input([], 0, false, true, true, false));
        Require(env.Step == GuideStep.EnvironmentBlocked, "missing backend blocks environment");
        Require(env.Title.Contains("后端组件"), "environment step names backend components");
        Require(env.Detail.Contains("prepared-next"), "environment step points to prepared-next folder");
        Require(env.Detail.Contains("_backup"), "environment step points to _backup folder");
        Require(env.Detail.Contains("拷"), "environment step tells user to copy folders");

        // Recovery / authorization flags absent -> need to relaunch via script.
        var restart = SetupGuide.Evaluate(new SetupGuide.Input(files, 1, true, false, false, false));
        Require(restart.Step == GuideStep.NeedRestart, "missing flags require relaunch");

        // No WinUSB card yet -> step 1 switch driver.
        var switchDriver = SetupGuide.Evaluate(new SetupGuide.Input(files, 0, false, true, true, false));
        Require(switchDriver.Step == GuideStep.SwitchDriver, "no WinUSB card -> switch driver step");
        Require(switchDriver.StepLabel.Contains("第 1 步"), "switch is labelled step 1");
        Require(switchDriver.PrimaryAction == "一键换 WinUSB", "switch primary action is the driver button");

        // WinUSB card but not pinned to libusb -> ask to rescan / replug.
        var rescan = SetupGuide.Evaluate(new SetupGuide.Input(files, 1, false, true, true, false));
        Require(rescan.Step == GuideStep.Rescan, "unmapped WinUSB -> rescan step");
        Require(rescan.Detail.Contains("拔"), "rescan tells user to replug");

        // All ready -> step 2 start receiving.
        var ready = SetupGuide.Evaluate(new SetupGuide.Input(files, 1, true, true, true, false));
        Require(ready.Step == GuideStep.StartReceiving, "all preconditions -> start receiving");
        Require(ready.StepLabel.Contains("第 2 步"), "start is labelled step 2");
        Require(ready.PrimaryAction == "开启接收", "start primary action is receive button");
        Require(ready.PrimaryActionVisible, "start primary action visible");

        // Session running -> step 3 and no primary action (avoid double start).
        var receiving = SetupGuide.Evaluate(new SetupGuide.Input(files, 1, true, true, true, true));
        Require(receiving.Step == GuideStep.Receiving, "running session -> receiving step");
        Require(receiving.StepLabel.Contains("第 3 步"), "receiving is labelled step 3");
        Require(!receiving.PrimaryActionVisible, "receiving hides primary action");
        Require(receiving.Detail.Contains("隔空投送"), "receiving explains AirDrop on iPhone");

        // Pure decision logic: evaluation does not touch the filesystem.
        var before = Directory.GetFiles(temp).Length;
        _ = SetupGuide.Evaluate(new SetupGuide.Input([], 0, false, false, false, false));
        Require(Directory.GetFiles(temp).Length == before, "guide evaluation is read-only");

        Console.WriteLine("PASS: setup guide steps the user clearly and stays read-only");
    }
}
