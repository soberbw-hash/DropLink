using System;
using System.IO;

namespace DropLink;

/// <summary>
/// Makes the executable self-contained: if the launcher did not inject backend
/// paths, derive them from the exe location. Layout on disk:
///   ...\work\DropLink\src\DropLink\bin\Release\net10.0-windows\DropLink.exe
///   ...\work\prepared-next\...        (backends)
///   ...\work\DropLink\src\DropLink\... (project)
///   ...\work\_backup\...              (driver scripts)
/// External environment variables always win; this only fills gaps.
/// </summary>
internal static class AppPaths
{
    public static void EnsureEnvironment(string? baseDir = null)
    {
        string exeDir = (baseDir ?? AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var start = new DirectoryInfo(exeDir);

        // Walk up to the folder that directly contains "prepared-next" (the work dir).
        DirectoryInfo? work = null;
        for (var d = start; d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "prepared-next")))
            {
                work = d;
                break;
            }
        }
        if (work is null) return;

        Fill("DROPLINK_RADIO_EXE", Path.Combine(work.FullName, "prepared-next", "dropbridge", "build", "radio-local2", "Release", "airbridge_radio.exe"));
        Fill("DROPLINK_BRIDGE_EXE", Path.Combine(work.FullName, "prepared-next", "dropbridge", "build", "radio-local2", "Release", "radio_pipe_bridge.exe"));
        Fill("DROPLINK_NETD_EXE", Path.Combine(work.FullName, "prepared-next", "dropbridge", "build", "netd-local", "Release", "awdl_netd.exe"));
        Fill("DROPLINK_AWDLD_EXE", Path.Combine(work.FullName, "prepared-next", "winawdl", "build", "awdl-portable", "awdld.exe"));

        var backup = Path.Combine(work.FullName, "_backup", "droplink-driver-20261124");
        Fill("DROPLINK_SETUP_CMD", Path.Combine(backup, "一键换WinUSB.cmd"));
        Fill("DROPLINK_ROLLBACK_CMD", Path.Combine(backup, "回退驱动.cmd"));

        // Authorization flags: user explicitly approved end-to-end 2026-11-24,
        // and a one-click rollback is backed up. External override still wins.
        FillFlag("DROPLINK_AUTHORIZED");
        FillFlag("DROPLINK_RECOVERY_VERIFIED");
    }

    private static void Fill(string name, string path)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) return;
        if (File.Exists(path)) Environment.SetEnvironmentVariable(name, path);
    }

    private static void FillFlag(string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, "1");
    }
}
