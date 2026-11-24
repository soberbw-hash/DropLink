using System.Diagnostics;
using System.IO;
using System.Text;

namespace DropLink;

internal sealed record DriverSwitchResult(bool Ok, int ExitCode, string Detail, string Script);

/// <summary>
/// The one place that runs the driver switch and its rollback. Both the setup
/// flow and the settings window call it, so the confirmation text, the timeout
/// and the failure detail cannot drift apart between the two.
/// </summary>
internal static class DriverSwitch
{
    internal static string? ScriptFor(bool toWinUsb)
    {
        var variable = toWinUsb ? "DROPLINK_SETUP_CMD" : "DROPLINK_ROLLBACK_CMD";
        var path = Environment.GetEnvironmentVariable(variable) ?? "";
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
    }

    internal static string Question(bool toWinUsb) => toWinUsb
        ? "把这张无线网卡切换成 WinUSB（接收隔空投送必需，一次性）。\n\n换完之后它不能当 Wi-Fi 用了，随时可以回退。\n大约 10~60 秒，中途不用管。\n\n继续？"
        : "把网卡恢复成原厂驱动。\n\n全自动，大约 10~30 秒。\n\n继续？";

    internal static async Task<DriverSwitchResult> RunAsync(bool toWinUsb)
    {
        var script = ScriptFor(toWinUsb)
            ?? throw new FileNotFoundException(toWinUsb ? "未找到换驱动脚本。" : "未找到回退脚本。");
        script = ResolvePowerShellScript(script);
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory
        };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Force" })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动脚本。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (Exception) { }
            return new DriverSwitchResult(false, -1, "驱动脚本 5 分钟没有结束，已中止。", script);
        }
        var outText = await stdout;
        var errText = await stderr;

        var detail = new StringBuilder();
        if (process.ExitCode != 0)
        {
            detail.AppendLine($"驱动操作没有成功（退出码 {process.ExitCode}）。");
            if (!string.IsNullOrWhiteSpace(outText)) detail.AppendLine().AppendLine(outText.Trim());
            if (!string.IsNullOrWhiteSpace(errText)) detail.AppendLine().AppendLine(errText.Trim());
            detail.AppendLine().AppendLine(LogTail(Path.Combine(Path.GetDirectoryName(script) ?? "", "setup.log")));
        }
        return new DriverSwitchResult(process.ExitCode == 0, process.ExitCode, detail.ToString().Trim(), script);
    }

    private static string ResolvePowerShellScript(string path)
    {
        if (path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) return path;
        var sibling = Path.ChangeExtension(path, ".ps1");
        return File.Exists(sibling) ? sibling : path;
    }

    private static string LogTail(string path, int lines = 12)
    {
        try
        {
            if (!File.Exists(path)) return "（没有生成日志，脚本可能没能启动）";
            var all = File.ReadAllLines(path);
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception error) { return "（日志读取失败：" + error.Message + "）"; }
    }
}
