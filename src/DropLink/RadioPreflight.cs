using System.IO;

namespace DropLink;

// Read-only decision logic. No driver, device or network operation is performed here.
internal sealed record RadioDevice(string InstanceId, string HardwareId, string Service, string Status);
internal sealed record RadioPreflightResult(bool Ready, IReadOnlyList<string> Problems);

internal static class RadioPreflight
{
    internal const string KnownHardwareId = "USB\\VID_0BDA&PID_C820&MI_02";

    internal static RadioPreflightResult Check(
        IReadOnlyList<RadioDevice> devices, string selectedInstanceId,
        bool explicitHardwareApproval, bool verifiedRecovery, IEnumerable<string> requiredFiles)
    {
        var errors = new List<string>();
        if (!explicitHardwareApproval) errors.Add("尚未明确授权初始化目标网卡并发送无线帧。");
        if (!verifiedRecovery) errors.Add("尚未确认原驱动信息及可行的恢复方法。");
        if (string.IsNullOrWhiteSpace(selectedInstanceId))
            errors.Add("未指定目标设备实例。");
        if (devices.Count(d => string.Equals(d.HardwareId, KnownHardwareId, StringComparison.OrdinalIgnoreCase)) != 1)
            errors.Add("同型号设备不是唯一的，无法保证无线进程选择正确设备。");
        var matches = devices.Where(d => string.Equals(d.InstanceId, selectedInstanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            errors.Add("目标设备不存在或无法唯一识别。");
        else
        {
            var target = matches[0];
            if (!string.Equals(target.HardwareId, KnownHardwareId, StringComparison.OrdinalIgnoreCase))
                errors.Add("硬件 ID 与已研究的 Wi-Fi 接口不匹配。");
            if (!string.Equals(target.Service, "WinUSB", StringComparison.OrdinalIgnoreCase))
                errors.Add("目标接口未绑定 WinUSB；不会自动更换驱动。");
            if (!string.Equals(target.Status, "OK", StringComparison.OrdinalIgnoreCase))
                errors.Add("目标接口状态异常。");
        }
        var files = requiredFiles.ToArray();
        if (files.Length == 0 || files.Any(string.IsNullOrWhiteSpace))
            errors.Add("后端文件清单不完整。");
        else if (files.Any(path => !File.Exists(path)))
            errors.Add("缺少所需的后端文件。");
        return new RadioPreflightResult(errors.Count == 0, errors);
    }
}
