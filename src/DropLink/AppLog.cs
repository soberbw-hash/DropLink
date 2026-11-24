using System;
using System.IO;

namespace DropLink;

/// <summary>
/// Minimal append-only log for host-side events that happen before or outside
/// the receiver engine (stale-chain cleanup, start failures). The engine keeps
/// its own log; this one only records what the WPF host does on its own.
/// </summary>
internal static class AppLog
{
    public static void Write(string message)
    {
        try
        {
            var path = Path.Combine(DataDirectory(), "logs", "app.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch (Exception) { /* logging must never break the app */ }
    }

    private static string DataDirectory()
    {
        var output = Environment.GetEnvironmentVariable("DROPLINK_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(output) &&
            Path.GetDirectoryName(output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } parent)
            return parent;
        var exe = Environment.ProcessPath;
        var appDir = string.IsNullOrWhiteSpace(exe)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        var root = Directory.GetParent(appDir)?.FullName ?? appDir;
        return Path.Combine(root, "data");
    }
}
