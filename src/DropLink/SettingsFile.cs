using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DropLink;

/// <summary>
/// The one place that reads data\settings.json for the host.
///
/// The engine reads the same file in Python and the settings page edits it as
/// JSON; the host only needs two answers out of it (where to save, whether the
/// user allowed auto-receive), so it asks here instead of growing a third
/// interpretation of the file.
/// </summary>
internal static class SettingsFile
{
    internal static string Path => System.IO.Path.Combine(AppPaths.DataDirectory(), "settings.json");

    internal static JsonObject Read()
    {
        try
        {
            if (!File.Exists(Path)) return new JsonObject();
            return JsonNode.Parse(File.ReadAllText(Path, Encoding.UTF8)) as JsonObject ?? new JsonObject();
        }
        catch (Exception error)
        {
            AppLog.Write("settings unreadable: " + error.Message);
            return new JsonObject();
        }
    }

    /// <summary>
    /// Writes the whole file atomically. A half-written settings.json would make
    /// the engine fall back to its defaults, which for auto-receive means the
    /// user's choice silently stops applying.
    /// </summary>
    internal static void Save(JsonObject settings)
    {
        var file = Path;
        var folder = System.IO.Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = file + ".tmp";
        // No BOM: the engine reads this file as utf-8, and a BOM would end up
        // inside the first key's name.
        File.WriteAllText(temp, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>Sets one key and saves. Used by the setup flow.</summary>
    internal static void Set(string key, JsonNode? value)
    {
        var settings = Read();
        settings[key] = value;
        Save(settings);
    }

    internal static bool Flag(string key, bool fallback = false) =>
        Read()[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    internal static string Text(string key) =>
        Read()[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>
    /// Where received files go. A folder chosen in the settings page wins over
    /// the launcher's default; the portable default stays data\received so an
    /// existing install keeps finding its files where it always did.
    /// </summary>
    internal static string ReceiveFolder()
    {
        var chosen = Read()["receive_folder"]?.ToString();
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            try
            {
                Directory.CreateDirectory(chosen);
                return chosen;
            }
            catch (Exception error)
            {
                // A folder on a disconnected drive must not stop the receiver.
                AppLog.Write("receive folder unusable, falling back: " + error.Message);
            }
        }
        var env = Environment.GetEnvironmentVariable("DROPLINK_OUTPUT_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        // Portable package: everything it owns stays inside its own folder.
        if (AppPaths.IsPortable())
            return System.IO.Path.Combine(AppPaths.PortableRoot(), "data", "received");
        // Ordinary install: received files belong in Downloads, where the user
        // already looks for things they just got. Never next to the executable.
        var downloads = AppPaths.DownloadsFolder();
        if (!string.IsNullOrWhiteSpace(downloads))
            return System.IO.Path.Combine(downloads, "DropLink");
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DropLink");
    }
    internal static Task OpenReceiveFolderAsync() => Task.Run(() =>
    {
        var folder = Directory.CreateDirectory(ReceiveFolder()).FullName;
        using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
        {
            UseShellExecute = true, Verb = "open"
        });
    });
}
