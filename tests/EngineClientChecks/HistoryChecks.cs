using DropLink;

/// <summary>
/// The recent list can now be cleared from the window, with an undo. An undo
/// that only remembered the three visible rows would quietly drop the rest of
/// the list, and one that did not survive a reload would resurrect rows the
/// file no longer has - so both are pinned here.
/// </summary>
internal static class HistoryChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static HistoryBatch Row(string id, int minutesAgo) => new()
    {
        Id = id,
        At = DateTime.Now.AddMinutes(-minutesAgo),
        Folder = Path.GetTempPath(),
        Files = [id + ".png"],
        Bytes = 1024,
        Sender = "iPhone",
        Kind = "image",
    };

    internal static void Run(string temp)
    {
        var path = Path.Combine(temp, "history-checks.json");
        if (File.Exists(path)) File.Delete(path);

        var history = new TransferHistory(path);
        // More rows than the window shows: Refresh keeps three, the file keeps all.
        for (var i = 0; i < 5; i++) history.Add(Row("r" + i, i));
        Require(history.Count == 5, "five rows stored, got " + history.Count);
        Require(history.Recent.Count == 3, "the window shows three, got " + history.Recent.Count);

        var snapshot = history.Snapshot();
        Require(snapshot.Count == 5, "snapshot keeps every row, got " + snapshot.Count);

        history.Clear();
        Require(history.Count == 0, "clear empties the list");
        Require(history.Recent.Count == 0, "clear empties the window list");

        history.Restore(snapshot);
        Require(history.Count == 5, "restore puts all five back, got " + history.Count);
        Require(history.Recent.Count == 3, "restore refills the window list");
        Require(history.Recent[0].Id == "r0", "newest row first after restore, got " + history.Recent[0].Id);

        // The undo has to be real: a new instance reads the same file.
        var reloaded = new TransferHistory(path);
        Require(reloaded.Count == 5, "restored rows were written to disk, got " + reloaded.Count);

        // Order of the snapshot is by id, not by time on the way back in, so the
        // window must still sort rather than trust the file's order.
        var shuffled = new List<HistoryBatch> { Row("late", 0), Row("early", 60) };
        reloaded.Restore(shuffled);
        Require(reloaded.Recent.Count == 2, "shorter list restores too");
        Require(reloaded.Recent[0].Id == "late", "restore still sorts newest first");

        Console.WriteLine("PASS: clearing the recent list is reversible and survives a reload");
    }
}
