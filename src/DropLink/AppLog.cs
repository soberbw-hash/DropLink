using System;
using System.Diagnostics;
using System.IO;

namespace DropLink;

/// <summary>
/// Append-only host log. It records what the WPF host does on its own — startup
/// timeline, stale-chain cleanup, backend stderr, start failures — so a problem
/// can be located by stage instead of guessed from a single error string.
/// Every line carries a short session id and a monotonic offset, which is what
/// makes "which stage took 40 seconds" answerable.
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly string Session = Guid.NewGuid().ToString("N")[..8];

    internal static string SessionId => Session;

    public static void Write(string message)
    {
        var offset = Clock.Elapsed.TotalSeconds;
        Append("app.log", $"T+{offset,6:0.0}s [{Session}] {message}");
    }

    /// <summary>A named point on the startup timeline; the offset is the whole point.</summary>
    public static void Mark(string stage) => Write("stage " + stage);

    /// <summary>Backend/engine stderr, kept out of the IPC stream and off the UI.</summary>
    public static void WriteEngineStderr(string line) => Append("engine-stderr.log", line);

    private static void Append(string fileName, string line)
    {
        try
        {
            var directory = Path.Combine(DataDirectory(), "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            lock (Gate)
            {
                Rotate(path);
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + Environment.NewLine);
            }
        }
        catch (Exception) { /* logging must never break the app */ }
    }

    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            var previous = path + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(path, previous);
        }
        catch (Exception) { /* rotation is best-effort */ }
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
