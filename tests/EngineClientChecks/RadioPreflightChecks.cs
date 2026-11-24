using DropLink;

internal static class RadioPreflightChecks
{
    internal static void Run(string temp)
    {
        var required = Path.Combine(temp, "radio.exe");
        File.WriteAllText(required, "placeholder"); // No executable is started.
        var good = new RadioDevice("USB\\VID_0BDA&PID_C820&MI_02\\instance", RadioPreflight.KnownHardwareId, "WinUSB", "OK");
        void Expect(bool expected, RadioPreflightResult result)
        {
            if (result.Ready != expected) throw new Exception("Unexpected preflight result: " + string.Join("; ", result.Problems));
        }
        Expect(true, RadioPreflight.Check([good], good.InstanceId, true, true, [required]));
        Expect(false, RadioPreflight.Check([good], good.InstanceId, false, true, [required]));
        Expect(false, RadioPreflight.Check([good], good.InstanceId, true, false, [required]));
        Expect(false, RadioPreflight.Check([good with { HardwareId = "USB\\VID_0BDA&PID_C820&MI_00" }], good.InstanceId, true, true, [required]));
        Expect(false, RadioPreflight.Check([good with { Service = "RtlWlanu" }], good.InstanceId, true, true, [required]));
        Expect(false, RadioPreflight.Check([good, good with { InstanceId = "different-instance" }], good.InstanceId, true, true, [required]));
        Expect(false, RadioPreflight.Check([good, good], good.InstanceId, true, true, [required]));
        Expect(false, RadioPreflight.Check([good], good.InstanceId, true, true, [Path.Combine(temp, "missing.exe")]));
        Expect(false, RadioPreflight.Check([good], good.InstanceId, true, true, []));
        Console.WriteLine("PASS: exact identity, driver, authorization, recovery and dependency checks");
    }
}
