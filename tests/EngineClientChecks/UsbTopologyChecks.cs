using DropLink;

internal static class UsbTopologyChecks
{
    private const string Hardware = "USB\\VID_0BDA&PID_C820&MI_02";
    private const string Instance = "USB\\VID_0BDA&PID_C820&MI_02\\7&1a2b3c4d&0&2";

    private static PnpRadioTarget Target(string location = "Port_#0002.Hub_#0004",
        string service = "WinUSB", string status = "OK", string hardware = Hardware, string instance = Instance)
        => new(instance, hardware, location, service, status);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("UsbTopology: " + message);
    }

    internal static void Run()
    {
        // ID parsing, including composite MI and case-insensitivity.
        Require(UsbTopology.TryParseHardwareId("USB\\VID_0BDA&PID_C820&MI_02\\inst", out var vid, out var pid, out var mi)
                && vid == 0x0bda && pid == 0xc820 && mi == 2, "parse VID/PID/MI");
        Require(UsbTopology.TryParseHardwareId("usb\\vid_0bda&pid_c811", out var v2, out var p2, out var mi2)
                && v2 == 0x0bda && p2 == 0xc811 && mi2 is null, "parse without MI");
        Require(!UsbTopology.TryParseHardwareId("garbage", out _, out _, out _), "reject malformed ID");

        // LocationInformation parsing.
        Require(UsbTopology.TryParseLocation("Port_#0002.Hub_#0004", out var port, out var hub)
                && port == 2 && hub == 4, "parse port and hub");
        Require(UsbTopology.TryParseLocation("Port_#12.Hub_#3", out port, out _) && port == 12, "parse unpadded port");
        Require(!UsbTopology.TryParseLocation("no location", out _, out _), "reject missing port");

        // Dotted port path parsing.
        Require(UsbTopology.TryLastPort("4.2.3", out var last) && last == 3, "last port segment");
        Require(UsbTopology.TryLastPort("2", out last) && last == 2, "single port segment");
        Require(!UsbTopology.TryLastPort("4.x", out _), "reject non-numeric port path");
        Require(!UsbTopology.TryLastPort("", out _), "reject empty port path");

        var right = new UsbDeviceSnapshot(1, "4.2", 0x0bda, 0xc820);
        var wrongPort = new UsbDeviceSnapshot(1, "4.5", 0x0bda, 0xc820);
        var otherId = new UsbDeviceSnapshot(1, "4.2", 0x0bda, 0xc811);

        // Single device on the matching port resolves.
        Require(UsbTopology.Resolve(Target(), [right]) is { Selected: true, Device: not null }, "single matching device");

        // Two cards, same VID/PID, different ports: topology picks the right one.
        var multi = UsbTopology.Resolve(Target(), [wrongPort, right]);
        Require(multi.Selected && multi.Device == right, "topology disambiguates identical VID/PID cards");

        // Re-enumeration: port path changes, fresh snapshots re-resolve cleanly.
        var moved = new UsbDeviceSnapshot(1, "4.9", 0x0bda, 0xc820);
        var afterMove = UsbTopology.Resolve(Target("Port_#0009.Hub_#0004"), [wrongPort, moved]);
        Require(afterMove.Selected && afterMove.Device == moved, "re-enumeration re-resolves with fresh topology");

        // Fail-closed cases.
        Require(!UsbTopology.Resolve(Target(), []).Selected, "no devices");
        Require(!UsbTopology.Resolve(Target(), [otherId]).Selected, "VID/PID mismatch");
        Require(!UsbTopology.Resolve(Target(), [wrongPort]).Selected, "port mismatch");
        Require(!UsbTopology.Resolve(Target(), [right, right with { PortPath = "7.2" }]).Selected,
            "two devices ending on same port -> ambiguous");
        Require(!UsbTopology.Resolve(Target(location: ""), [right]).Selected, "no LocationInformation");
        Require(!UsbTopology.Resolve(Target(service: "RtlWlanu"), [right]).Selected, "wrong driver binding");
        Require(!UsbTopology.Resolve(Target(status: "Error"), [right]).Selected, "abnormal device status");
        Require(!UsbTopology.Resolve(Target(instance: "USB\\VID_0BDA&PID_C820&MI_00\\inst"), [right]).Selected,
            "Bluetooth interface not Wi-Fi");
        Require(!UsbTopology.Resolve(Target(hardware: "USB\\VID_0BDA&PID_C820&MI_00"), [right]).Selected,
            "hardware ID MI mismatch");

        Console.WriteLine("PASS: PnP->libusb topology mapping, multi-card, re-enumeration and fail-closed checks");
    }
}
