using System.Runtime.InteropServices;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DropLink;

/// <summary>
/// One read-only look at this machine: which cards exist, whether the backend
/// files are in place, and whether the current process is allowed to use them.
/// Nothing here opens a device, writes a setting or starts a process - the
/// setup flow and the main window must be able to disagree about nothing.
/// </summary>
internal sealed record ReadinessSnapshot(
    IReadOnlyList<RadioDevice> Devices,
    IReadOnlyList<string> RequiredFiles,
    bool Authorized,
    bool RecoveryVerified,
    int ReadyWinUsbCards,
    int SupportedCards,
    RadioDevice? Pnp,
    UsbDeviceSnapshot? Target,
    bool Legacy)
{
    internal bool HasTarget =>
        (Target is not null && BackendChain.ValidatedTargetSelection(Target)) || (Legacy && Pnp is not null);

    internal SetupGuide.Input ToGuideInput(bool sessionRunning, bool starting) =>
        new(RequiredFiles, ReadyWinUsbCards, HasTarget, Authorized, RecoveryVerified, sessionRunning, starting);
}

internal static class ReadinessProbe
{
    internal static bool EnvFlag(string name) =>
        Environment.GetEnvironmentVariable(name) is string v
        && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    internal static string[] RequiredFilePaths() =>
        new[] { "DROPLINK_RADIO_EXE", "DROPLINK_BRIDGE_EXE", "DROPLINK_NETD_EXE", "DROPLINK_AWDLD_EXE" }
            .Select(k => Environment.GetEnvironmentVariable(k) ?? "").ToArray();

    internal static async Task<ReadinessSnapshot> TakeAsync()
    {
        var devices = await DevicesAsync();
        var files = RequiredFilePaths();
        var radioExe = Environment.GetEnvironmentVariable("DROPLINK_RADIO_EXE") ?? "";
        var inventory = !devices.Any(IsReadyCard) || string.IsNullOrWhiteSpace(radioExe)
            ? new DeviceInventory.Result([], "未配置后端程序路径")
            : await Task.Run(() => DeviceInventory.Read(radioExe));
        var (pnp, target, legacy) = PickTarget(devices, inventory);
        return new ReadinessSnapshot(
            devices,
            files,
            EnvFlag("DROPLINK_AUTHORIZED"),
            EnvFlag("DROPLINK_RECOVERY_VERIFIED"),
            devices.Count(d => IsReadyCard(d)),
            devices.Count(d => RadioPreflight.IsSupportedHardwareId(d.HardwareId)),
            pnp,
            target,
            legacy);
    }

    internal static bool IsReadyCard(RadioDevice device) =>
        RadioPreflight.IsSupportedHardwareId(device.HardwareId)
        && string.Equals(device.Service, "WinUSB", StringComparison.OrdinalIgnoreCase)
        && string.Equals(device.Status, "OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pairs the Windows device with the libusb entry for the same physical
    /// port. A card bound to WinUSB is not enough: the backend opens the libusb
    /// device, and with two identical adapters plugged in, guessing is how the
    /// wrong one gets claimed.
    /// </summary>
    internal static (RadioDevice? Device, UsbDeviceSnapshot? Target, bool Legacy) PickTarget(
        IReadOnlyList<RadioDevice> devices, DeviceInventory.Result inventory)
    {
        var ready = devices.Where(IsReadyCard)
            .OrderBy(d => d.LocationInfo, StringComparer.OrdinalIgnoreCase).ToArray();
        if (ready.Length == 0) return (null, null, false);
        if (inventory.Error is not null)
            return ready.Length == 1 ? (ready[0], null, true) : (ready[0], null, false);
        foreach (var pnp in ready)
        {
            if (!UsbTopology.TryParseLocation(pnp.LocationInfo, out var port, out _)) continue;
            var match = inventory.Devices.Where(d => RadioPreflight.IsSupportedUsbId(d.Vid, d.Pid)
                && UsbTopology.TryLastPort(d.PortPath, out var last) && last == port).ToArray();
            if (match.Length == 1) return (pnp, match[0], false);
        }
        return (ready[0], null, false);
    }

    internal static Task<IReadOnlyList<RadioDevice>> DevicesAsync() => Task.Run(EnumerateUsb);

    private static IReadOnlyList<RadioDevice> EnumerateUsb()
    {
        const uint presentUsb = 0x101; // CM_GETIDLIST_FILTER_ENUMERATOR | PRESENT
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Check(CM_Get_Device_ID_List_SizeW(out var length, "USB", presentUsb));
            var buffer = new char[Math.Max(2, length)];
            var result = CM_Get_Device_ID_ListW("USB", buffer, buffer.Length, presentUsb);
            if (result == 0x1A) continue; // CR_BUFFER_SMALL after a hot plug.
            Check(result);
            var devices = new List<RadioDevice>();
            foreach (var instance in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var hardware = instance.Split('\\').ElementAtOrDefault(1);
                if (hardware is null || !Regex.IsMatch(hardware, @"^VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(&MI_[0-9A-F]{2})?$", RegexOptions.IgnoreCase))
                    continue;
                var located = CM_Locate_DevNodeW(out var node, instance, 0);
                if (located == 0x0D) continue; // Device disappeared during enumeration.
                Check(located);
                var ok = CM_Get_DevNode_Status(out var flags, out var problem, node, 0) == 0
                    && (flags & 0x400) == 0 && problem == 0;
                devices.Add(new RadioDevice(instance, "USB\\" + hardware, Property(node, 5),
                    ok ? "OK" : "Unknown", Property(node, 14)));
            }
            return devices;
        }
        throw new IOException("USB 设备列表正在变化。");
    }

    private static void Check(uint result)
    {
        if (result != 0) throw new IOException($"读取 USB 设备信息失败 (CONFIGRET 0x{result:X})。");
    }

    private static string Property(uint node, uint property)
    {
        uint bytes = 0;
        if (CM_Get_DevNode_Registry_PropertyW(node, property, out _, null, ref bytes, 0) != 0x1A || bytes == 0)
            return "";
        var buffer = new byte[bytes];
        return CM_Get_DevNode_Registry_PropertyW(node, property, out var type, buffer, ref bytes, 0) == 0 && type == 1
            ? Encoding.Unicode.GetString(buffer, 0, (int)bytes).TrimEnd('\0') : "";
    }

    [DllImport("cfgmgr32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out int length, string filter, uint flags);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, int length, uint flags);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);
    [DllImport("cfgmgr32", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type,
        [Out] byte[]? buffer, ref uint bytes, uint flags);

    internal static string LogTail(string path, int lines = 12)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var all = File.ReadAllLines(path);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception) { return ""; }
    }
}
