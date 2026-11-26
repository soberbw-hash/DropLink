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
    /// <summary>
    /// The portable root: the folder that holds backend\ next to the exe. Every
    /// path the app writes (data\settings.json, data\logs, data\received) hangs
    /// off this, so it has to be derived the same way everywhere.
    ///
    /// Found by walking up rather than by "parent of the exe folder": the exe
    /// ships either in app\ or directly in the root beside backend\, and a
    /// layout change used to send data\ to the parent of the whole package -
    /// i.e. onto the Desktop - with nothing to show for it.
    /// </summary>
    public static string PortableRoot()
    {
        var exe = Environment.ProcessPath;
        var start = new DirectoryInfo(string.IsNullOrWhiteSpace(exe)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory);
        return FindRoot(start)?.FullName ?? start.Parent?.FullName ?? start.FullName;
    }

    private static DirectoryInfo? FindRoot(DirectoryInfo start)
    {
        var bundled = new DirectoryInfo(Path.Combine(start.FullName, "runtime"));
        if (File.Exists(Path.Combine(bundled.FullName, "backend", "airbridge_radio.exe"))) return bundled;
        for (var d = start; d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "backend", "airbridge_radio.exe")) ||
                Directory.Exists(Path.Combine(d.FullName, "prepared-next"))) return d;
        }
        return null;
    }

    /// <summary>
    /// The data folder. A launcher-injected DROPLINK_OUTPUT_DIR wins, because
    /// that is the folder the receiver was actually told to save into; without
    /// it the portable root decides.
    /// </summary>
    public static string DataDirectory()
    {
        var output = Environment.GetEnvironmentVariable("DROPLINK_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(output) &&
            Path.GetDirectoryName(output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } parent)
            return parent;
        return Path.Combine(PortableRoot(), "data");
    }

    /// <summary>
    /// True when the app found its own backend folder next to the exe, i.e. it
    /// is the portable package. Everything the package owns lives inside that
    /// folder; anything else is an ordinary install and must not write next to
    /// its own executable (Program Files is not writable).
    /// </summary>
    public static bool IsPortable()
    {
        var exe = Environment.ProcessPath;
        var start = new DirectoryInfo(string.IsNullOrWhiteSpace(exe)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory);
        return FindRoot(start) is not null;
    }

    /// <summary>
    /// The real Downloads folder, via the shell's known-folder API. The folder
    /// can be moved or redirected (OneDrive), so guessing %USERPROFILE%\Downloads
    /// puts files somewhere the user will not look.
    /// </summary>
    public static string? DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out buffer) != 0) return null;
            var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception) { return null; }
        finally
        {
            if (buffer != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(buffer);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

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
        var work = FindRoot(start);
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
            // The launcher sets this; a direct exe start (the scheduled task,
            // a shortcut) does not, and then the engine's log silently lands
            // nowhere. Diagnosis needs that file.
            FillDirectory("DROPLINK_LOG_DIR", Path.Combine(work.FullName, "data", "logs"));
            Fill("DROPLINK_HARDWARE_FILE", Path.Combine(work.FullName, "data", "hardware.json"));
            // Same name the old launcher put in the AirDrop list (COMPUTERNAME).
            // data\settings.json still wins when it names a device.
            FillValue("DROPLINK_DEVICE_NAME", Environment.MachineName);
            EnsureSettings(Path.Combine(work.FullName, "data"));

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

    /// <summary>
    /// data\settings.json has to exist before the engine starts, because the
    /// engine reads auto_receive from it and its own default is "ask every
    /// time". The old launcher copied the default in; without a launcher a
    /// fresh package would sit there asking for confirmation on every photo.
    ///
    /// The env var the launcher also set (DROPLINK_AUTO_RECEIVE) is deliberately
    /// NOT set here: it overrides the file unconditionally, which is why turning
    /// auto-receive off in the settings page had no effect for as long as the
    /// launcher was used.
    /// </summary>
    private static void EnsureSettings(string dataDirectory)
    {
        var settings = Path.Combine(dataDirectory, "settings.json");
        var fallback = Path.Combine(dataDirectory, "settings.default.json");
        if (File.Exists(settings) || !File.Exists(fallback)) return;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.Copy(fallback, settings);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
