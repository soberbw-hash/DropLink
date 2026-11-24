using System.IO;
using System.Text;

namespace DropLink;

// Read-only readiness report shown when the user presses the receive button.
// It never starts a process, opens a device, changes a driver or sends frames.
// It reuses RadioPreflight (the same gate the real chain uses) so what the UI
// says matches what BackendChain would actually allow.
internal static class ReadinessReport
{
    internal readonly record struct Input(
        IReadOnlyList<RadioDevice> Devices,
        string SelectedInstance,
        IReadOnlyList<string> RequiredFiles,
        bool Authorized,
        bool RecoveryVerified);

    internal static string Build(Input input)
    {
        var sb = new StringBuilder();
        sb.AppendLine("接收条件检查（只读 · 未更改硬件/驱动/网络）");
        sb.AppendLine();

        sb.AppendLine("一、USB 设备（PnP 只读查询）");
        if (input.Devices.Count == 0) sb.AppendLine("  （未发现相关设备）");
        foreach (var d in input.Devices)
            sb.AppendLine($"  · {d.HardwareId}  服务={d.Service}  状态={d.Status}");
        sb.AppendLine();

        sb.AppendLine("二、后端程序");
        var files = input.RequiredFiles;
        if (files.Count == 0 || files.All(string.IsNullOrWhiteSpace))
            sb.AppendLine("  （未配置后端程序路径，设置 DROPLINK_*_EXE 环境变量后显示）");
        else
            foreach (var f in files)
                sb.AppendLine($"  · {(string.IsNullOrWhiteSpace(f) ? "未配置" : (File.Exists(f) ? "就绪" : "缺失"))}  {(string.IsNullOrWhiteSpace(f) ? "" : f)}");
        sb.AppendLine();

        var check = RadioPreflight.Check(input.Devices, input.SelectedInstance,
            input.Authorized, input.RecoveryVerified, files);
        sb.AppendLine("三、结论");
        if (check.Ready)
        {
            sb.AppendLine("  已满足开启接收的前置条件。");
            sb.AppendLine("  点击“开启接收”会启动链路并初始化该网卡；不会自动启动。");
        }
        else
        {
            sb.AppendLine("  暂不能开启接收，尚缺：");
            foreach (var problem in check.Problems) sb.AppendLine("  ✗ " + problem);
        }
        return sb.ToString();
    }
}
