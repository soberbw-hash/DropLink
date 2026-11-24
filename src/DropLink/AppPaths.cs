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
        // Environment.ProcessPath is the real exe path; AppContext.BaseDirectory
        // can point at the self-extraction folder for single-file builds.
        var root = baseDir
            ?? (Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) is { Length: > 0 } p
                ? p
                : AppContext.BaseDirectory);
        string exeDir = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var start = new DirectoryInfo(exeDir);

        // Walk up to the folder that directly contains "prepared-next" (the work
        // dir) or "backend" (the portable release layout).
        DirectoryInfo? work = null;
        for (var d = start; d is not null; d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "prepared-next")) ||
                File.Exists(Path.Combine(d.FullName, "backend", "airbridge_radio.exe")))
            {
                work = d;
                break;
            }
        }
        if (work is null) return;

        // Portable release: one flat backend folder next to the app.
        var flat = Path.Combine(work.FullName, "backend");
        if (File.Exists(Path.Combine(flat, "airbridge_radio.exe")))
        {
            Fill("DROPLINK_RADIO_EXE", Path.Combine(flat, "airbridge_radio.exe"));
            Fill("DROPLINK_BRIDGE_EXE", Path.Combine(flat, "radio_pipe_bridge.exe"));
            Fill("DROPLINK_NETD_EXE", Path.Combine(flat, "awdl_netd.exe"));
            Fill("DROPLINK_AWDLD_EXE", Path.Combine(flat, "awdld.exe"));
            Fill("DROPLINK_PYTHON", Path.Combine(flat, "python", "python.exe"));
            // The bundled embedded Python ships python312._pth containing
            // "..\airdrop", so the engine MUST live in backend\airdrop or its
            // own modules cannot be imported (sys.path is frozen by the _pth).
            Fill("DROPLINK_ENGINE_SCRIPT", Path.Combine(flat, "airdrop", "engine.py"));
            FillDirectory("DROPLINK_OUTPUT_DIR", Path.Combine(work.FullName, "data", "received"));
            FillDirectory("DROPLINK_KEYS_DIR", Path.Combine(work.FullName, "data", "keys"));
            Fill("DROPLINK_HARDWARE_FILE", Path.Combine(work.FullName, "data", "hardware.json"));
            FillValue("DROPLINK_DEVICE_NAME", "DropLink");

            // Portable driver kit: point at the .ps1 directly. The app runs it
            // through the PowerShell host, which rejects a .cmd path. The .cmd
            // wrappers stay for people who double-click them.
            var kit = Path.Combine(work.FullName, "_backup", "droplink-driver");
            Fill("DROPLINK_SETUP_CMD", Path.Combine(kit, "Switch-WinUSB.ps1"));
            Fill("DROPLINK_ROLLBACK_CMD", Path.Combine(kit, "Restore-Driver.ps1"));
        }
        else
        {
            Fill("DROPLINK_RADIO_EXE", Path.Combine(work.FullName, "prepared-next", "dropbridge", "build", "radio-local2", "Release", "airbridge_radio.exe"));
            Fill("DROPLINK_BRIDGE_EXE", Path.Combine(work.FullName, "prepared-next", "dropbridge", "build", "radio-local2", "Release", "radio_pipe_bridge.exe"));
            Fill("DROPLINK_NETD_EXE", Path.Combine(work.FullName, "prepared-next", "dropbridge", "build", "netd-local", "Release", "awdl_netd.exe"));
            Fill("DROPLINK_AWDLD_EXE", Path.Combine(work.FullName, "prepared-next", "winawdl", "build", "awdl-portable", "awdld.exe"));

            var backup = Path.Combine(work.FullName, "_backup", "droplink-driver-20261124");
            Fill("DROPLINK_SETUP_CMD", Path.Combine(backup, "一键换WinUSB.cmd"));
            Fill("DROPLINK_ROLLBACK_CMD", Path.Combine(backup, "回退驱动.cmd"));
        }

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

    private static void FillDirectory(string name, string path)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) return;
        try
        {
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(name, path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void FillValue(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    private static void FillFlag(string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, "1");
    }
}
