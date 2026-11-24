using System.Globalization;
using System.Text.RegularExpressions;

namespace DropLink;

// Read-only snapshot of one libusb device as reported by a REAL enumeration.
// Bus and PortPath are opaque libusb values (libusb_get_bus_number /
// libusb_get_port_numbers joined with '.'). This project never fabricates them;
// a caller must supply values observed from the actual device list.
internal sealed record UsbDeviceSnapshot(int Bus, string PortPath, ushort Vid, ushort Pid);

// Read-only description of the PnP interface the user selected. All fields come
// from a query-only WMI/SetupAPI scan; nothing here opens, resets or binds a device.
internal sealed record PnpRadioTarget(
    string InstanceId, string HardwareId, string LocationInfo, string Service, string Status);

// Fail-closed outcome. Selected is true only when exactly one libusb device can
// be proven to match the chosen PnP interface by VID/PID plus USB topology.
internal sealed record UsbTopologyResult(
    bool Selected, UsbDeviceSnapshot? Device, IReadOnlyList<string> Problems);

// Maps a Windows PnP radio interface to one libusb device by USB topology.
// This is pure decision logic: no driver, device, network or process operation.
//
// Why this exists: the upstream radio opens with libusb_open_device_with_vid_pid,
// which picks the first matching device. With two identical VID/PID adapters the
// selected PnP instance and the device actually opened can differ. devourer can
// already open strictly by bus + dotted port path (DEVOURER_USB_BUS / PORT), but
// nothing bridged PnP identity to that topology. This resolver performs that
// bridge conservatively and refuses to guess.
internal static class UsbTopology
{
    // RTL8821CU composite device: MI_02 is Wi-Fi, MI_00 is Bluetooth.
    internal const int ExpectedWifiInterface = 2;

    private static readonly Regex IdPattern = new(
        @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})(?:&MI_([0-9A-Fa-f]{2}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PortPattern = new(
        @"Port_#?0*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HubPattern = new(
        @"Hub_#?0*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Parse VID/PID and optional MI from a hardware or instance ID string.
    internal static bool TryParseHardwareId(string text, out ushort vid, out ushort pid, out int? mi)
    {
        vid = 0; pid = 0; mi = null;
        var match = IdPattern.Match(text ?? "");
        if (!match.Success) return false;
        vid = ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        pid = ushort.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (match.Groups[3].Success)
            mi = int.Parse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    // Parse PnP LocationInformation such as "Port_#0002.Hub_#0004".
    // The hub value is informational only: Windows libusb does not expose a hub
    // number that maps to it, so it is never used to select a device.
    internal static bool TryParseLocation(string text, out int port, out int hub)
    {
        port = 0; hub = 0;
        var portMatch = PortPattern.Match(text ?? "");
        if (!portMatch.Success) return false;
        port = int.Parse(portMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var hubMatch = HubPattern.Match(text ?? "");
        if (hubMatch.Success) hub = int.Parse(hubMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        return true;
    }

    // Return the last segment of a dotted libusb port path ("4.2" -> 2), which
    // is the port of the device on its immediate parent hub - the same hop PnP
    // LocationInformation reports as Port_#.
    internal static bool TryLastPort(string portPath, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(portPath)) return false;
        var segments = portPath.Split('.');
        if (segments.Length == 0) return false;
        foreach (var segment in segments)
            if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return false;
        return int.TryParse(segments[^1], NumberStyles.None, CultureInfo.InvariantCulture, out port);
    }

    // Resolve the unique libusb device for a selected PnP interface.
    // Fails closed whenever identity, binding, interface or topology is
    // ambiguous or unverifiable. A re-enumeration simply calls this again with
    // fresh snapshots; nothing is cached across plug/unplug.
    internal static UsbTopologyResult Resolve(PnpRadioTarget target, IReadOnlyList<UsbDeviceSnapshot> devices)
    {
        var problems = new List<string>();

        if (!string.Equals(target.Service, "WinUSB", StringComparison.OrdinalIgnoreCase))
            problems.Add("目标接口未绑定 WinUSB；不会自动更换驱动。");
        if (!string.Equals(target.Status, "OK", StringComparison.OrdinalIgnoreCase))
            problems.Add("目标接口状态异常。");

        // Prefer the explicit hardware ID; fall back to the instance ID, which
        // also carries VID/PID/MI for composite devices.
        if (!TryParseHardwareId(target.HardwareId, out var vid, out var pid, out var hardwareMi) &&
            !TryParseHardwareId(target.InstanceId, out vid, out pid, out hardwareMi))
        {
            problems.Add("无法从 PnP 标识解析 VID/PID。");
            return new UsbTopologyResult(false, null, problems);
        }
        if (hardwareMi is not null && hardwareMi.Value != ExpectedWifiInterface)
            problems.Add("所选接口不是 Wi-Fi 接口（MI_02）；不会选择蓝牙或复合父设备。");
        if (!TryParseHardwareId(target.InstanceId, out _, out _, out var instanceMi) || instanceMi != ExpectedWifiInterface)
            problems.Add("所选 PnP 实例不是 MI_02 接口。");

        if (!TryParseLocation(target.LocationInfo, out var locationPort, out _))
        {
            problems.Add("缺少可解析的 PnP LocationInformation，无法在实例级确认设备。");
            return new UsbTopologyResult(false, null, problems);
        }

        var candidates = devices.Where(d => d.Vid == vid && d.Pid == pid).ToArray();
        if (candidates.Length == 0)
        {
            problems.Add("libusb 枚举中没有匹配 VID/PID 的设备。");
            return new UsbTopologyResult(false, null, problems);
        }

        // Topology is mandatory, not a tie-breaker: even a single VID/PID match
        // must agree with the PnP port before we claim instance-level selection.
        var matched = candidates.Where(d => TryLastPort(d.PortPath, out var last) && last == locationPort).ToArray();
        if (matched.Length == 0)
        {
            problems.Add("libusb 端口路径与 PnP 端口不匹配。");
            return new UsbTopologyResult(false, null, problems);
        }
        if (matched.Length > 1)
        {
            problems.Add("多个同 VID/PID 设备落在相同端口，无法唯一确定目标。");
            return new UsbTopologyResult(false, null, problems);
        }

        return new UsbTopologyResult(problems.Count == 0, problems.Count == 0 ? matched[0] : null, problems);
    }
}
