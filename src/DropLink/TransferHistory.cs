using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DropLink;

/// <summary>One completed transfer, as the window's recent list shows it.</summary>
internal sealed class HistoryBatch
{
    public string Id { get; set; } = "";
    /// <summary>Local time, written after the files were actually on disk.</summary>
    public DateTime At { get; set; }
    public string Folder { get; set; } = "";
    public string[] Files { get; set; } = [];
    public long Bytes { get; set; }
    public string Sender { get; set; } = "";
    public string Kind { get; set; } = "file";

    [JsonIgnore] public string Title => Files.Length switch
    {
        0 => "文件",
        1 => Path.GetFileName(Files[0]),
        _ => Kind switch
        {
            "image" => $"{Files.Length} 张照片",
            "video" => $"{Files.Length} 段视频",
            _ => $"{Files.Length} 个文件"
        }
    };

    [JsonIgnore] public string Extension => Files.Length == 1 && Title.LastIndexOf('.') > 0
        ? Path.GetExtension(Title) : "";
    [JsonIgnore] public string NameStem => Title[..(Title.Length - Extension.Length)];

    [JsonIgnore] public string Subtitle => Bytes > 0 ? $"{TransferItem.Size(Bytes)} · {When}" : When;

    [JsonIgnore]
    public string When
    {
        get
        {
            var span = DateTime.Now - At;
            if (span.TotalSeconds < 60) return "刚刚";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
            if (At.Date == DateTime.Today) return At.ToString("HH:mm");
            if (At.Date == DateTime.Today.AddDays(-1)) return "昨天 " + At.ToString("HH:mm");
            return At.ToString("MM-dd HH:mm");
        }
    }

    /// <summary>The row can outlive the files: they were moved or deleted since.</summary>
    [JsonIgnore]
    public bool Missing
    {
        get
        {
            if (Files.Length == 0) return !Directory.Exists(Folder);
            return !Files.Any(name => File.Exists(Path.Combine(Folder, name)));
        }
    }

    /// <summary>Path used by "open" and by the folder jump. A single file is
    /// opened as a file, a batch as its folder.</summary>
    [JsonIgnore]
    public string Target => Files.Length == 1 ? Path.Combine(Folder, Files[0]) : Folder;
}

/// <summary>
/// Bounded local history of what was received. Written only after the files are
/// on disk, so the list can never claim a transfer that did not happen.
///
/// It is deliberately a small JSON file, not a database: a few hundred rows of
/// text do not justify a dependency, and a corrupt file must cost nothing more
/// than an empty list.
/// </summary>
internal sealed class TransferHistory
{
    private const int Limit = 100;
    private readonly string path;
    private readonly List<HistoryBatch> items = [];

    public TransferHistory(string path)
    {
        this.path = path;
        Load();
    }

    public ObservableCollection<HistoryBatch> Recent { get; } = [];

    /// <summary>Rows to show in the window: newest first, at most three.</summary>
    public void Refresh(int take = 3)
    {
        Recent.Clear();
        foreach (var item in items.OrderByDescending(x => x.At).Take(take)) Recent.Add(item);
    }

    public void Add(HistoryBatch batch)
    {
        items.RemoveAll(x => x.Id == batch.Id);
        items.Add(batch);
        if (items.Count > Limit) items.RemoveRange(0, items.Count - Limit);
        Save();
        Refresh();
    }

    public void Remove(HistoryBatch batch)
    {
        items.RemoveAll(x => x.Id == batch.Id);
        Save();
        Refresh();
    }

    /// <summary>Clears the list. Received files are untouched - the history is a
    /// convenience, not storage.</summary>
    public void Clear()
    {
        items.Clear();
        Save();
        Refresh();
    }

    /// <summary>The whole list, so a clear can be undone. Refresh only shows the
    /// newest three; undoing from the window has to put all of them back.</summary>
    public List<HistoryBatch> Snapshot() => [.. items];

    public void Restore(IReadOnlyList<HistoryBatch> snapshot)
    {
        items.Clear();
        items.AddRange(snapshot);
        Save();
        Refresh();
    }

    public int Count => items.Count;

    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<List<HistoryBatch>>(File.ReadAllText(path));
            if (loaded is null) return;
            items.AddRange(loaded.Where(x => !string.IsNullOrWhiteSpace(x.Id) && x.Files is not null));
        }
        catch (Exception error)
        {
            // A broken history file must never stop the receiver. Keep the bad
            // copy so it can be inspected instead of silently overwritten.
            AppLog.Write("history unreadable: " + error.Message);
            try { File.Move(path, path + ".bad", overwrite: true); } catch (Exception) { }
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items,
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            // Atomic: a half-written history is worse than an old one.
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error)
        {
            AppLog.Write("history save failed: " + error.Message);
        }
    }

    /// <summary>Builds a batch from the engine's 'stored' event. Sizes come from
    /// the files themselves - the archive size in the event is not the size the
    /// user sees in Explorer.</summary>
    public static HistoryBatch From(string id, string folder, IEnumerable<string> names, string sender)
    {
        var files = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Path.GetFileName).OfType<string>().ToArray();
        long bytes = 0;
        foreach (var name in files)
        {
            try
            {
                var info = new FileInfo(Path.Combine(folder, name));
                if (info.Exists) bytes += info.Length;
            }
            catch (Exception) { /* a vanished file is not a reason to drop the row */ }
        }
        return new HistoryBatch
        {
            Id = id,
            At = DateTime.Now,
            Folder = folder,
            Files = files,
            Bytes = bytes,
            Sender = sender,
            Kind = KindOf(files)
        };
    }

    private static string KindOf(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return "file";
        var images = new[] { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".bmp", ".tif", ".tiff" };
        var videos = new[] { ".mov", ".mp4", ".m4v", ".avi", ".mkv", ".hevc" };
        var extensions = files.Select(f => Path.GetExtension(f).ToLowerInvariant()).ToArray();
        if (extensions.All(images.Contains)) return "image";
        if (extensions.Any(videos.Contains)) return "video";
        return "file";
    }
}
