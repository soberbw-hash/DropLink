using System.IO;
using System.Text.Json;

namespace DropLink;

/// <summary>
/// Hardware allow-list loaded from data/hardware.json. Unknown devices are
/// refused when the file is present; the built-in list is only a compatibility
/// fallback for tools/tests that run without the portable layout.
/// </summary>
internal sealed class HardwarePolicy
{
    internal bool AllowUntested { get; }
    internal string Source { get; }
    private readonly HashSet<string> _ids;
    private readonly HashSet<(ushort Vid, ushort Pid)> _usb;

    private static readonly string[] FallbackIds =
    [
        "USB\\VID_0BDA&PID_C820&MI_02",
        "USB\\VID_0BDA&PID_C811"
    ];

    private static HardwarePolicy? _current;
    internal static HardwarePolicy Current => _current ??= Load();

    private HardwarePolicy(bool allowUntested, string source, IEnumerable<string> ids,
                           IEnumerable<(ushort, ushort)> usb)
    {
        AllowUntested = allowUntested;
        Source = source;
        _ids = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        _usb = new HashSet<(ushort, ushort)>(usb);
    }

    internal bool AllowsHardwareId(string id) => _ids.Contains(id);
    internal bool AllowsUsbId(ushort vid, ushort pid) => _usb.Contains((vid, pid));

    internal static HardwarePolicy Load() => Load(FindPath());

    internal static HardwarePolicy Load(string? path)
    {
        if (path is null)
            return new HardwarePolicy(false, "builtin", FallbackIds,
                [(0x0bda, 0xc820), (0x0bda, 0xc811)]);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var allow = root.TryGetProperty("allow_untested", out var a) && a.ValueKind == JsonValueKind.True;
            var ids = new List<string>();
            var usbIds = new List<(ushort, ushort)>();
            if (root.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
            {
                foreach (var device in devices.EnumerateArray())
                {
                    var status = device.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                    if (!allow && !string.Equals(status, "verified", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (device.TryGetProperty("hardware_id", out var h))
                    {
                        var id = h.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) ids.Add(id!);
                    }
                    if (device.TryGetProperty("vid_pid", out var v))
                    {
                        var text = v.GetString() ?? "";
                        var parts = text.Split(':');
                        if (parts.Length == 2 &&
                            ushort.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var vid) &&
                            ushort.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var pid))
                            usbIds.Add((vid, pid));
                    }
                }
            }
            return new HardwarePolicy(allow, path, ids, usbIds);
        }
        catch
        {
            // A broken policy file must not silently widen the allow-list.
            return new HardwarePolicy(false, "invalid:" + path, [], []);
        }
    }

    private static string? FindPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("DROPLINK_HARDWARE_FILE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        var start = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        for (var dir = start; !string.IsNullOrWhiteSpace(dir); dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "data", "hardware.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
