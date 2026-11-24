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

        // BackendChain maps a resolved target to the radio's strict-topology
        // environment variables. Without a target, legacy VID/PID behavior is
        // preserved (no AIRBRIDGE_USB_BUS/PORT keys).
        var legacyEnv = BackendChain.BuildRadioEnvironment(null);
        Require(!legacyEnv.ContainsKey("AIRBRIDGE_USB_BUS") && !legacyEnv.ContainsKey("AIRBRIDGE_USB_PORT"),
            "no topology env without a resolved target");
        Require(legacyEnv["AIRBRIDGE_VID"] == "0x0bda" && legacyEnv["AIRBRIDGE_PID"] == "0xc811",
            "legacy VID/PID defaults (C811 is the adapter in use)");
        var strictEnv = BackendChain.BuildRadioEnvironment(new UsbDeviceSnapshot(3, "1.4.2", 0x0bda, 0xc820));
        Require(strictEnv["AIRBRIDGE_USB_BUS"] == "3" && strictEnv["AIRBRIDGE_USB_PORT"] == "1.4.2",
            "strict topology env from resolved target");
        Require(strictEnv["AIRBRIDGE_VID"] == "0x0bda" && strictEnv["AIRBRIDGE_PID"] == "0xc820",
            "VID/PID formatted from target");
        var busOnly = BackendChain.BuildRadioEnvironment(new UsbDeviceSnapshot(2, "", 0x0bda, 0xc820));
        Require(busOnly.ContainsKey("AIRBRIDGE_USB_BUS") && !busOnly.ContainsKey("AIRBRIDGE_USB_PORT"),
            "bus-only selection omits empty port");

        // Read-only inventory parsing (sample from the real --list-devices output:
        // two 0BDA:C811 cards on ports 8 and 5, plus non-radio devices).
        var inventoryJson = "[{\"bus\":1,\"port\":\"\",\"vid\":32902,\"pid\":31456}," +
            "{\"bus\":1,\"port\":\"8\",\"vid\":3034,\"pid\":51217}," +
            "{\"bus\":1,\"port\":\"4\",\"vid\":11145,\"pid\":2322}," +
            "{\"bus\":1,\"port\":\"5\",\"vid\":3034,\"pid\":51217}]";
        var inventory = DeviceInventory.Parse(inventoryJson);
        Require(inventory.Error is null && inventory.Devices.Count == 4, "parse inventory JSON");
        var cards = inventory.Devices.Where(d => d.Vid == 0x0bda && d.Pid == 0xc811).ToArray();
        Require(cards.Length == 2, "two identical-VID/PID cards enumerated");
        // A PnP target on port 5 resolves to exactly the second card.
        var cardTarget = new PnpRadioTarget(
            "USB\\VID_0BDA&PID_C811\\123456", "USB\\VID_0BDA&PID_C811",
            "Port_#0005.Hub_#0001", "WinUSB", "OK");
        var picked = UsbTopology.Resolve(cardTarget, inventory.Devices);
        Require(picked.Selected && picked.Device == cards[1], "inventory feeds topology to pick correct card");
        Require(DeviceInventory.Parse("{}").Error is not null, "reject non-array inventory");
        Require(DeviceInventory.Parse("not json").Error is not null, "reject malformed inventory");

        // C811 is non-composite: a whole-device WinUSB binding has no MI suffix.
        // A composite-style C811 should be rejected, while the plain C811 binds.
        Require(UsbTopology.Resolve(new PnpRadioTarget(
                    "USB\\VID_0BDA&PID_C811&MI_02\\inst", "USB\\VID_0BDA&PID_C811&MI_02",
                    "Port_#0005.Hub_#0001", "WinUSB", "OK"), [cards[1]]).Selected == false,
            "non-composite C811 with MI is rejected");
        Require(UsbTopology.Resolve(cardTarget, [cards[0], cards[1]]).Selected
                && UsbTopology.Resolve(cardTarget, [cards[0], cards[1]]).Device == cards[1],
            "plain C811 whole-device target resolves to the correct port");

        // Safety gate: opens only for a real, supported, physical target;
        // fails closed for null, unsupported VID/PID, missing bus/port or junk.
        Require(BackendChain.ValidatedTargetSelection(new UsbDeviceSnapshot(1, "8", 0x0bda, 0xc811)),
            "gate opens for real C811 target");
        Require(BackendChain.ValidatedTargetSelection(new UsbDeviceSnapshot(1, "4.2", 0x0bda, 0xc820)),
            "gate opens for real C820 target");
        Require(!BackendChain.ValidatedTargetSelection(null), "gate closed for null target");
        Require(!BackendChain.ValidatedTargetSelection(new UsbDeviceSnapshot(0, "8", 0x0bda, 0xc811)),
            "gate closed for invalid bus");
        Require(!BackendChain.ValidatedTargetSelection(new UsbDeviceSnapshot(1, "", 0x0bda, 0xc811)),
            "gate closed for missing port");
        Require(!BackendChain.ValidatedTargetSelection(new UsbDeviceSnapshot(1, "x.y", 0x0bda, 0xc811)),
            "gate closed for malformed port");
        Require(!BackendChain.ValidatedTargetSelection(new UsbDeviceSnapshot(1, "8", 0x1d6b, 0x0002)),
            "gate closed for unsupported VID/PID");

        Console.WriteLine("PASS: PnP->libusb topology mapping, multi-card, re-enumeration, fail-closed, C811 non-composite, safety gate and read-only inventory checks");
    }
}
