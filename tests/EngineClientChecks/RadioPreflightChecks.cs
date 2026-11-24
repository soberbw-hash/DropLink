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

        // RTL8811CU (0bda:c811) is the same Jaguar2/8821C silicon, bound as a
        // whole device (no &MI_02). It must pass with WinUSB while still
        // refusing the stock Realtek driver.
        var c811 = new RadioDevice("USB\\VID_0BDA&PID_C811\\5&2AF31B16&0&8", "USB\\VID_0BDA&PID_C811", "WinUSB", "OK");
        Expect(true, RadioPreflight.Check([c811], c811.InstanceId, true, true, [required]));
        Expect(false, RadioPreflight.Check([c811 with { Service = "RtlWlanu" }], c811.InstanceId, true, true, [required]));
        if (!RadioPreflight.IsSupportedUsbId(0x0bda, 0xc811) ||
            !RadioPreflight.IsSupportedUsbId(0x0bda, 0xc820) ||
            RadioPreflight.IsSupportedUsbId(0x1d6b, 0x0002))
            throw new Exception("supported USB id table is wrong");
        // Hardware policy is data-driven: a verified C811 passes while an
        // upstream-only C820 is refused unless allow_untested is enabled.
        var policyPath = Path.Combine(temp, "hardware.json");
        File.WriteAllText(policyPath, """
            {"allow_untested":false,"devices":[
              {"hardware_id":"USB\\VID_0BDA&PID_C811","vid_pid":"0BDA:C811","status":"verified"},
              {"hardware_id":"USB\\VID_0BDA&PID_C820&MI_02","vid_pid":"0BDA:C820","status":"upstream_only"}
            ]}
            """);
        var strict = HardwarePolicy.Load(policyPath);
        if (!strict.AllowsHardwareId("USB\\VID_0BDA&PID_C811") ||
            !strict.AllowsUsbId(0x0bda, 0xc811) ||
            strict.AllowsHardwareId("USB\\VID_0BDA&PID_C820&MI_02") ||
            strict.AllowsUsbId(0x0bda, 0xc820))
            throw new Exception("strict hardware policy is wrong");
        File.WriteAllText(policyPath, """
            {"allow_untested":true,"devices":[
              {"hardware_id":"USB\\VID_0BDA&PID_C811","vid_pid":"0BDA:C811","status":"verified"},
              {"hardware_id":"USB\\VID_0BDA&PID_C820&MI_02","vid_pid":"0BDA:C820","status":"upstream_only"}
            ]}
            """);
        var loose = HardwarePolicy.Load(policyPath);
        if (!loose.AllowsUsbId(0x0bda, 0xc820))
            throw new Exception("allow_untested did not widen hardware policy");
        Console.WriteLine("PASS: exact identity, C811/C820 chips, driver, authorization, recovery, dependency and hardware policy checks");
    }
}
