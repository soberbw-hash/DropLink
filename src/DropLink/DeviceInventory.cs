using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DropLink;

// Read-only bridge to the radio's --list-devices inventory mode. That mode
// enumerates libusb devices and prints JSON WITHOUT opening, resetting or
// claiming anything — no driver, firmware or radio operation. This class only
// runs that mode and parses its output into UsbDeviceSnapshot values so
// UsbTopology can resolve a selected PnP instance to a topology target.
internal static class DeviceInventory
{
    internal sealed record Result(IReadOnlyList<UsbDeviceSnapshot> Devices, string? Error);

    internal static Result Read(string radioExecutable)
    {
        if (!File.Exists(radioExecutable))
            return new Result([], "后端程序不存在：" + radioExecutable);
        var info = new ProcessStartInfo(Path.GetFullPath(radioExecutable))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(radioExecutable))!
        };
        info.ArgumentList.Add("--list-devices");
        var process = Process.Start(info);
        if (process is null) return new Result([], "无法启动设备枚举。");
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(true); return new Result([], "设备枚举超时。"); }
            var json = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) return new Result([], "设备枚举失败（退出码 " + process.ExitCode + "）。");
            return Parse(json);
        }
    }

    // Parse is separate so it is unit-testable without launching the tool.
    internal static Result Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new Result([], "设备枚举输出格式错误。");
            var devices = new List<UsbDeviceSnapshot>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!TryNumber(item, "bus", out int bus) || !TryNumber(item, "vid", out int vid) ||
                    !TryNumber(item, "pid", out int pid))
                    continue;
                if (bus < 0 || bus > 255 || vid < 0 || vid > ushort.MaxValue || pid < 0 || pid > ushort.MaxValue)
                    continue;
                var port = item.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "" : "";
                devices.Add(new UsbDeviceSnapshot(bus, port, (ushort)vid, (ushort)pid));
            }
            return new Result(devices, null);
        }
        catch (JsonException ex) { return new Result([], "设备枚举输出解析失败：" + ex.Message); }
    }

    private static bool TryNumber(JsonElement obj, string key, out int value)
    {
        value = 0;
        return obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }
}
