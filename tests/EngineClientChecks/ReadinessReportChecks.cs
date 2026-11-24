using DropLink;

internal static class ReadinessReportChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    internal static void Run(string temp)
    {
        var exe = Path.Combine(temp, "radio.exe");
        File.WriteAllText(exe, "x");
        var target = new RadioDevice("USB\\VID_0BDA&PID_C820&MI_02\\1", RadioPreflight.KnownHardwareId, "WinUSB", "OK");

        // Ready: exactly one C820 MI_02 on WinUSB, files present, authorized + recovery.
        var ready = ReadinessReport.Build(new ReadinessReport.Input(
            [target], target.InstanceId, [exe], true, true));
        Require(ready.Contains("已满足开启接收的前置条件"), "ready report says preconditions met");
        Require(ready.Contains("不会自动启动"), "ready report still warns about safety lock");

        // C811 / Realtek is not accepted: report must name the mismatch.
        var wrong = new RadioDevice("USB\\VID_0BDA&PID_C811\\5", "USB\\VID_0BDA&PID_C811", "RtlWlanu", "OK");
        var blocked = ReadinessReport.Build(new ReadinessReport.Input(
            [wrong], wrong.InstanceId, [exe], true, true));
        Require(blocked.Contains("暂不能开启接收"), "wrong hardware blocks receiving");
        Require(blocked.Contains("硬件 ID 与已研究的 Wi-Fi 接口不匹配"), "report names hardware mismatch");
        Require(blocked.Contains("未绑定 WinUSB"), "report names driver mismatch");

        // Missing authorization and recovery are called out explicitly.
        var noAuth = ReadinessReport.Build(new ReadinessReport.Input(
            [target], target.InstanceId, [exe], false, false));
        Require(noAuth.Contains("尚未明确授权"), "report calls out missing authorization");
        Require(noAuth.Contains("尚未确认原驱动信息"), "report calls out missing recovery");

        // Read-only: building a report must never create or open anything.
        var before = Directory.GetFiles(temp).Length;
        _ = ReadinessReport.Build(new ReadinessReport.Input([wrong], "", [], false, false));
        Require(Directory.GetFiles(temp).Length == before, "report is read-only");

        Console.WriteLine("PASS: readiness report reflects real preconditions and stays read-only");
    }
}
