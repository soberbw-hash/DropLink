using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;

namespace DropLink;

internal sealed class TransferRow : INotifyPropertyChanged
{
    private string status = "等待确认";
    private string progress = "";
    public required string Id { get; init; }
    public required string Sender { get; init; }
    public required string Files { get; init; }
    public string Status { get => status; set { status = value; Changed(nameof(Status)); } }
    public string Progress { get => progress; set { progress = value; Changed(nameof(Progress)); } }
    public bool Finished { get; set; }
    public bool Accepted { get; set; }
    public bool Receiving { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

// All calls must be made on the UI thread. The engine supplies all transfer facts.
internal sealed class TransferState
{
    internal ObservableCollection<TransferRow> Items { get; } = [];
    internal TransferRow? Apply(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !ReadString(message, "event", out var kind) ||
            !ReadString(message, "id", out var id) || string.IsNullOrWhiteSpace(id)) return null;
        var row = Items.FirstOrDefault(x => x.Id == id);
        if (kind == "asked")
        {
            if (row is not null || !message.TryGetProperty("waits", out var waits) || waits.ValueKind != JsonValueKind.True) return null;
            ReadString(message, "sender", out var sender);
            var names = new List<string>();
            if (message.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                foreach (var file in files.EnumerateArray().Take(10))
                    if (file.ValueKind == JsonValueKind.Object && ReadString(file, "name", out var name)) names.Add(name);
            row = new TransferRow { Id = id, Sender = string.IsNullOrWhiteSpace(sender) ? "未知设备" : sender,
                Files = names.Count == 0 ? "文件" : string.Join("、", names) };
            Items.Insert(0, row);
            return row;
        }
        if (row is null || row.Finished) return null;
        switch (kind)
        {
            case "answered":
                if (!ReadString(message, "how", out var how)) return null;
                row.Accepted = how == "accepted";
                row.Status = row.Accepted ? "等待传输" : "未接收";
                row.Finished = how != "accepted";
                break;
            case "incoming":
                if (!row.Accepted) return null;
                row.Receiving = true;
                row.Status = "正在接收";
                if (ReadNumber(message, "done", out long done) && done >= 0)
                {
                    row.Progress = ReadNumber(message, "total", out long total) && total > 0
                        ? $"{Math.Min(100, (double)done / total * 100):0}%" : $"{done:N0} 字节";
                }
                break;
            case "stored": if (!row.Receiving) return null; row.Status = "已完成"; row.Progress = ""; row.Finished = true; break;
            case "lost": row.Status = "接收失败"; row.Progress = ""; row.Finished = true; break;
            default: return null;
        }
        return row;
    }
    private static bool ReadString(JsonElement obj, string key, out string result)
    {
        result = "";
        if (!obj.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return false;
        result = value.GetString() ?? ""; return true;
    }
    private static bool ReadNumber(JsonElement obj, string key, out long number)
    {
        number = 0;
        return obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number);
    }
}
