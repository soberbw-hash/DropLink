using System.Diagnostics;
using System.IO;
using System.Text.Json;

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
        var inventory = string.IsNullOrWhiteSpace(radioExe)
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

    internal static async Task<IReadOnlyList<RadioDevice>> DevicesAsync()
    {
        const string script = "Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match '^USB\\\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}' } | ForEach-Object { $id=$_.InstanceId; $svc=(Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data; $loc=(Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_LocationInfo' -ErrorAction SilentlyContinue).Data; [pscustomobject]@{ InstanceId=$id; Hardware=($id -replace '^.*?(VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&MI_[0-9A-F]{2})?).*','USB\\$1'); Service=[string]$svc; Status=$_.Status; Location=[string]$loc } } | ConvertTo-Json -Compress";
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动设备查询。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("设备查询超时。"); }
        var json = await stdout;
        _ = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("设备查询失败。");
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToArray()
            : new[] { doc.RootElement };
        var result = new List<RadioDevice>();
        foreach (var item in items)
        {
            var hardware = item.GetProperty("Hardware").GetString() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(hardware,
                    @"^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(&MI_[0-9A-F]{2})?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            var location = item.TryGetProperty("Location", out var locProp) ? locProp.GetString() ?? "" : "";
            result.Add(new RadioDevice(
                item.GetProperty("InstanceId").GetString() ?? "",
                hardware,
                item.GetProperty("Service").GetString() ?? "",
                item.GetProperty("Status").GetString() ?? "",
                location));
        }
        return result;
    }

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
