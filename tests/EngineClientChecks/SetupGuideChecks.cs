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

        // Missing backend -> environment blocked. The detail has to name the
        // folder, because "reopen the app" is the wrong instruction when a file
        // is genuinely absent.
        var env = SetupGuide.Evaluate(new SetupGuide.Input([], 0, false, true, true, false, false));
        Require(env.Step == GuideStep.EnvironmentBlocked, "missing backend blocks environment");
        Require(env.Title.Contains("文件不完整"), "environment step says files are incomplete");
        Require(env.Detail.Contains("backend"), "environment step names the backend folder");

        // A listed path that does not exist is the same failure as an empty one.
        var phantom = new[] { files[0], files[1], files[2], Path.Combine(temp, "gone.exe") };
        Require(SetupGuide.Evaluate(new SetupGuide.Input(phantom, 1, true, true, true, false, false)).Step
                == GuideStep.EnvironmentBlocked, "a path that does not exist still blocks");

        // Recovery / authorization flags absent -> relaunch.
        var restart = SetupGuide.Evaluate(new SetupGuide.Input(files, 1, true, false, false, false, false));
        Require(restart.Step == GuideStep.NeedRestart, "missing flags require relaunch");
        Require(restart.PrimaryActionVisible, "relaunch offers a button");

        // No WinUSB card yet -> switch the driver.
        var switchDriver = SetupGuide.Evaluate(new SetupGuide.Input(files, 0, false, true, true, false, false));
        Require(switchDriver.Step == GuideStep.SwitchDriver, "no WinUSB card -> switch driver step");
        Require(switchDriver.PrimaryAction == "开始配置", "switch primary action opens the setup flow");
        // The title has to stay on one line at the narrowest window width, which
        // is why it was shortened from "完成一次配置，即可开始隔空投送".
        Require(switchDriver.Title.Length <= 12, "switch title stays short enough for one line");

        // WinUSB card but not pinned to libusb -> ask to replug.
        var rescan = SetupGuide.Evaluate(new SetupGuide.Input(files, 1, false, true, true, false, false));
        Require(rescan.Step == GuideStep.Rescan, "unmapped WinUSB -> rescan step");
        Require(rescan.Detail.Contains("拔"), "rescan tells the user to replug");

        // All ready -> start receiving.
        var ready = SetupGuide.Evaluate(new SetupGuide.Input(files, 1, true, true, true, false, false));
        Require(ready.Step == GuideStep.StartReceiving, "all preconditions -> start receiving");
        Require(ready.PrimaryAction == "开启接收", "start primary action is the receive button");
        Require(ready.PrimaryActionVisible, "start primary action visible");

        // A chain that is coming up must not also offer a start button: that
        // pressed the button under a line that already said it was working, and
        // started a second chain on top of the first.
        var starting = SetupGuide.Evaluate(new SetupGuide.Input(files, 1, true, true, true, false, true));
        Require(starting.Step == GuideStep.Starting, "starting chain reports its own step");
        Require(!starting.PrimaryActionVisible, "starting hides the start button");

        // Session running -> no primary action, so it cannot be started twice.
        var receiving = SetupGuide.Evaluate(new SetupGuide.Input(files, 1, true, true, true, true, false));
        Require(receiving.Step == GuideStep.Receiving, "running session -> receiving step");
        Require(!receiving.PrimaryActionVisible, "receiving hides primary action");
        Require(receiving.Detail.Contains("隔空投送"), "receiving explains AirDrop on the iPhone");

        // Pure decision logic: evaluation does not touch the filesystem.
        var before = Directory.GetFiles(temp).Length;
        _ = SetupGuide.Evaluate(new SetupGuide.Input([], 0, false, false, false, false, false));
        Require(Directory.GetFiles(temp).Length == before, "guide evaluation is read-only");

        Console.WriteLine("PASS: setup guide steps the user clearly and stays read-only");
    }
}
