using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DropLink;

/// <summary>
/// Where one incoming transfer is. The UI reads this, never the raw event, so a
/// repeated or out-of-order event cannot make the window say something the
/// transfer is not actually doing.
/// </summary>
internal enum TransferStage
{
    /// <summary>Accepted (or auto-accepted); the bytes have not started yet.</summary>
    Accepted,
    /// <summary>Needs the user's answer before anything is uploaded.</summary>
    Awaiting,
    Receiving,
    /// <summary>Upload finished, files are being unpacked and written.</summary>
    Saving,
    Done,
    Failed
}

internal sealed class TransferItem : INotifyPropertyChanged
{
    private TransferStage stage = TransferStage.Accepted;
    private long doneBytes;
    private long? totalBytes;
    private double bytesPerSecond;
    private string? failure;

    public required string Id { get; init; }
    public string Sender { get; init; } = "iPhone";
    public string[] Names { get; init; } = [];
    public int FileCount { get; init; }
    public bool RequiresConfirmation { get; init; }
    public DateTime StartedAt { get; } = DateTime.Now;

    /// <summary>Monotonic start, used for the transfer clock. Never shown raw.</summary>
    public long StartedTicks { get; } = Stopwatch.GetTimestamp();

    public TransferStage Stage
    {
        get => stage;
        set { if (stage == value) return; stage = value; Changed(nameof(Stage)); Changed(nameof(IsFinished)); }
    }

    public long DoneBytes
    {
        get => doneBytes;
        set { if (doneBytes == value) return; doneBytes = value; Changed(nameof(DoneBytes)); Changed(nameof(Fraction)); Changed(nameof(ReceivedText)); }
    }

    public long? TotalBytes
    {
        get => totalBytes;
        set { if (totalBytes == value) return; totalBytes = value; Changed(nameof(TotalBytes)); Changed(nameof(Fraction)); Changed(nameof(PercentText)); Changed(nameof(HasPercent)); }
    }

    /// <summary>Smoothed, never the raw per-event delta: the engine throttles its
    /// progress events, so a single delta is a spike, not a speed.</summary>
    public double BytesPerSecond
    {
        get => bytesPerSecond;
        set { if (Math.Abs(bytesPerSecond - value) < 1) return; bytesPerSecond = value; Changed(nameof(BytesPerSecond)); Changed(nameof(SpeedText)); Changed(nameof(RemainingText)); }
    }

    public string? Failure
    {
        get => failure;
        set { failure = value; Changed(nameof(Failure)); }
    }

    public bool IsFinished => stage is TransferStage.Done or TransferStage.Failed;

    public double Fraction
    {
        get
        {
            if (totalBytes is not > 0) return 0;
            return Math.Clamp((double)doneBytes / totalBytes.Value, 0, 1);
        }
    }

    public bool HasPercent => totalBytes is > 0 && stage is TransferStage.Receiving or TransferStage.Saving;

    public string PercentText => HasPercent ? $"{Fraction * 100:0}%" : "";

    public string CountText
    {
        get
        {
            // The engine can announce an upload without a file list (the sender
            // skipped the ask); claiming "1 个文件" there would be a guess.
            if (FileCount <= 0) return "文件";
            if (FileCount == 1)
            {
                var name = Names.FirstOrDefault();
                return string.IsNullOrEmpty(name) ? "1 个文件" : Path.GetFileName(name);
            }
            return $"{FileCount} 个文件";
        }
    }

    public string ReceivedText
    {
        get
        {
            if (totalBytes is > 0) return $"{Size(doneBytes)} / {Size(totalBytes.Value)}";
            return doneBytes > 0 ? $"已接收 {Size(doneBytes)}" : "";
        }
    }

    public string SpeedText => bytesPerSecond >= 1 ? Size((long)bytesPerSecond) + "/s" : "";

    public string RemainingText
    {
        get
        {
            if (totalBytes is not > 0 || bytesPerSecond < 1) return "";
            var left = Math.Max(0, totalBytes.Value - doneBytes) / bytesPerSecond;
            if (left < 1) return "还剩 1 秒内";
            if (left > 3600) return "";
            return left < 60 ? $"还剩约 {left:0} 秒" : $"还剩约 {left / 60:0} 分";
        }
    }

    /// <summary>One sentence for the header, the tray tooltip and the taskbar.</summary>
    public string Headline => stage switch
    {
        TransferStage.Awaiting => "收到发送请求",
        TransferStage.Accepted => "准备接收",
        TransferStage.Receiving => "正在接收",
        TransferStage.Saving => "正在保存文件",
        TransferStage.Done => "接收完成",
        _ => "接收中断"
    };

    public static string Size(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double value = bytes;
        foreach (var unit in new[] { "KB", "MB", "GB", "TB" })
        {
            value /= 1024;
            if (value < 1024) return value < 10 ? $"{value:0.0} {unit}" : $"{value:0} {unit}";
        }
        return $"{value:0} PB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Turns engine events into transfer rows. Every transfer gets a row from the
/// moment it is announced, in both auto-receive and confirm modes - the old code
/// only created a row on an 'asked' event that was waiting for an answer, so an
/// auto-accepted transfer had no row and its progress was silently dropped.
///
/// All calls must be made on the UI thread.
/// </summary>
internal sealed class TransferState
{
    private readonly Dictionary<string, (long Ticks, long Bytes)> samples = [];

    internal ObservableCollection<TransferItem> Items { get; } = [];

    /// <summary>The transfer the window should be showing, if any.</summary>
    internal TransferItem? Active => Items.FirstOrDefault(x => !x.IsFinished);

    internal TransferItem? Apply(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;
        if (!ReadString(message, "event", out var kind)) return null;
        if (!ReadString(message, "id", out var id) || string.IsNullOrWhiteSpace(id)) return null;

        var item = Items.FirstOrDefault(x => x.Id == id);
        switch (kind)
        {
            case "transfer_created":
                if (item is not null) return null;   // duplicate: keep the first facts
                item = Create(message, id);
                Items.Insert(0, item);
                return item;
            case "answered":
                if (item is null || item.IsFinished) return null;
                if (!ReadString(message, "how", out var how)) return null;
                if (how == "accepted") item.Stage = TransferStage.Accepted;
                else
                {
                    item.Stage = TransferStage.Failed;
                    item.Failure = how switch
                    {
                        "declined" => "已拒绝",
                        "left" => "发送方取消了",
                        "expired" => "超时没有答复",
                        _ => "没有接收"
                    };
                }
                return item;
            case "incoming":
                if (item is null) return null;
                if (item.Stage is TransferStage.Done or TransferStage.Failed) return null;
                item.Stage = TransferStage.Receiving;
                if (ReadNumber(message, "done", out var done) && done >= 0) Update(item, done);
                if (ReadNumber(message, "total", out var total) && total > 0) item.TotalBytes = total;
                // Upload finished, unpacking has not reported back yet. Showing
                // "100%" with nothing happening is the state users read as a hang.
                if (item.TotalBytes is > 0 && item.DoneBytes >= item.TotalBytes)
                    item.Stage = TransferStage.Saving;
                return item;
            case "stored":
                if (item is null || item.Stage == TransferStage.Done) return null;
                item.Stage = TransferStage.Done;
                item.BytesPerSecond = 0;
                if (item.TotalBytes is null && item.DoneBytes > 0) item.TotalBytes = item.DoneBytes;
                return item;
            case "lost":
                if (item is null || item.IsFinished) return null;
                item.Stage = TransferStage.Failed;
                item.Failure = ReadString(message, "reason", out var reason) ? Friendly(reason) : "传输中断";
                return item;
            default:
                return null;
        }
    }

    private TransferItem Create(JsonElement message, string id)
    {
        var names = new List<string>();
        var files = 0;
        if (message.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var file in array.EnumerateArray())
            {
                files++;
                if (file.ValueKind == JsonValueKind.Object && ReadString(file, "name", out var name))
                    names.Add(Path.GetFileName(name));
            }
        if (ReadNumber(message, "files_count", out var counted) && counted > files) files = (int)counted;
        ReadString(message, "sender", out var sender);
        var waits = message.TryGetProperty("requires_confirmation", out var flag) && flag.ValueKind == JsonValueKind.True;
        var item = new TransferItem
        {
            Id = id,
            Sender = string.IsNullOrWhiteSpace(sender) ? "iPhone" : sender,
            Names = names.ToArray(),
            FileCount = Math.Max(files, names.Count),
            RequiresConfirmation = waits,
            Stage = waits ? TransferStage.Awaiting : TransferStage.Accepted
        };
        if (ReadNumber(message, "total", out var total) && total > 0) item.TotalBytes = total;
        return item;
    }

    // Speed from a monotonic clock and a smoothed rate. The engine sends a
    // progress line every ~250 ms at most, and network bursts make the raw
    // delta jump; a 0.7/0.3 blend reads as a speed instead of a flicker.
    private void Update(TransferItem item, long done)
    {
        var now = Stopwatch.GetTimestamp();
        item.DoneBytes = done;
        if (samples.TryGetValue(item.Id, out var last))
        {
            var seconds = (now - last.Ticks) / (double)Stopwatch.Frequency;
            if (seconds >= 0.05 && done >= last.Bytes)
            {
                var instant = (done - last.Bytes) / seconds;
                item.BytesPerSecond = item.BytesPerSecond < 1 ? instant : item.BytesPerSecond * 0.7 + instant * 0.3;
            }
        }
        samples[item.Id] = (now, done);
    }

    private static string Friendly(string reason) => reason switch
    {
        "nothing arrived" => "没有收到数据",
        _ => reason
    };

    internal void Remove(string id)
    {
        var item = Items.FirstOrDefault(x => x.Id == id);
        if (item is not null) Items.Remove(item);
        samples.Remove(id);
    }

    /// <summary>Keeps the live list bounded: the window shows one transfer and
    /// the recent list is a separate, persisted history.</summary>
    internal void Trim(int keep = 6)
    {
        foreach (var stale in Items.Where(x => x.IsFinished).Skip(keep).ToArray())
            Items.Remove(stale);
    }

    private static bool ReadString(JsonElement obj, string key, out string result)
    {
        result = "";
        if (!obj.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return false;
        result = value.GetString() ?? "";
        return true;
    }

    private static bool ReadNumber(JsonElement obj, string key, out long number)
    {
        number = 0;
        return obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number);
    }
}
