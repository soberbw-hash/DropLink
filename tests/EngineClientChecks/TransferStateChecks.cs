using System.Text.Json;
using DropLink;

internal static class TransferStateChecks
{
    private static JsonElement Event(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    internal static void Run()
    {
        var state = new TransferState();

        // An event for a transfer that was never announced must not invent a row.
        Require(state.Apply(Event("{\"event\":\"stored\",\"id\":\"r1\"}")) is null,
            "an event without a transfer_created is ignored");

        // Every transfer gets its row from the announcement, in both modes. The
        // old code only created a row for an 'asked' event, so an auto-accepted
        // transfer had nowhere to show its progress.
        var auto = state.Apply(Event(
            "{\"event\":\"transfer_created\",\"id\":\"r1\",\"sender\":\"Phone\"," +
            "\"requires_confirmation\":false,\"files\":[{\"name\":\"a.jpg\"}],\"total\":100}"))
            ?? throw new Exception("auto-accepted transfer got no row");
        Require(auto.Stage == TransferStage.Accepted, "auto-accepted transfer starts accepted");
        Require(!auto.RequiresConfirmation, "auto-accepted transfer does not ask");

        // Duplicate announcement keeps the first facts instead of resetting them.
        state.Apply(Event("{\"event\":\"incoming\",\"id\":\"r1\",\"done\":50,\"total\":100}"));
        Require(state.Apply(Event(
                "{\"event\":\"transfer_created\",\"id\":\"r1\",\"requires_confirmation\":true}")) is null,
            "a repeated transfer_created is ignored");
        Require(auto.Stage == TransferStage.Receiving, "repeated announcement does not rewind the row");

        Require(auto.PercentText == "50%", "progress shows a percentage");
        Require(auto.SpeedText.Length > 0 || auto.DoneBytes == 50, "progress records the byte count");

        // Completion.
        state.Apply(Event("{\"event\":\"stored\",\"id\":\"r1\"}"));
        Require(auto.Stage == TransferStage.Done, "stored completes the row");
        Require(auto.IsFinished, "a completed transfer is finished");

        // A confirmation-mode transfer.
        var asked = state.Apply(Event(
            "{\"event\":\"transfer_created\",\"id\":\"r2\",\"sender\":\"iPhone\"," +
            "\"requires_confirmation\":true,\"files\":[{\"name\":\"b.heic\"}],\"total\":200}"))
            ?? throw new Exception("confirmation transfer got no row");
        Require(asked.Stage == TransferStage.Awaiting, "a transfer that asks starts awaiting");

        // Declining is a failure with a reason, not a silent disappearance.
        state.Apply(Event("{\"event\":\"answered\",\"id\":\"r2\",\"how\":\"declined\"}"));
        Require(asked.Stage == TransferStage.Failed, "decline fails the row");
        Require(asked.Failure == "已拒绝", "decline states why");

        // Accepting, then a repeated answer must be idempotent.
        var live = state.Apply(Event(
            "{\"event\":\"transfer_created\",\"id\":\"r3\",\"requires_confirmation\":true,\"total\":100}"))
            ?? throw new Exception("second confirmation transfer got no row");
        state.Apply(Event("{\"event\":\"answered\",\"id\":\"r3\",\"how\":\"accepted\"}"));
        Require(live.Stage == TransferStage.Accepted, "accept moves the row forward");
        state.Apply(Event("{\"event\":\"answered\",\"id\":\"r3\",\"how\":\"accepted\"}"));
        Require(live.Stage == TransferStage.Accepted, "a repeated accept changes nothing");

        // Paused is announced first and declined second, so a decline has to be
        // able to fail a row that is already accepted - the conformance vector
        // "paused_declines_after_announcing" depends on exactly this.
        var paused = state.Apply(Event(
            "{\"event\":\"transfer_created\",\"id\":\"r5\",\"requires_confirmation\":false}"))
            ?? throw new Exception("paused transfer got no row");
        Require(paused.Stage == TransferStage.Accepted, "an auto transfer starts accepted");
        state.Apply(Event("{\"event\":\"answered\",\"id\":\"r5\",\"how\":\"declined\"}"));
        Require(paused.Stage == TransferStage.Failed, "a decline after an auto-accept fails the row");

        // Progress that reaches the total means "unpacking", not "done": showing
        // 100% while nothing happens is the state people read as a hang.
        state.Apply(Event("{\"event\":\"incoming\",\"id\":\"r3\",\"done\":100,\"total\":100}"));
        Require(live.Stage == TransferStage.Saving, "reaching the total moves to saving, not done");
        state.Apply(Event("{\"event\":\"stored\",\"id\":\"r3\"}"));
        Require(live.Stage == TransferStage.Done, "stored finishes a saving transfer");

        // A lost link fails the row and is explained in words.
        var broken = state.Apply(Event(
            "{\"event\":\"transfer_created\",\"id\":\"r4\",\"requires_confirmation\":false}"))
            ?? throw new Exception("third transfer got no row");
        state.Apply(Event("{\"event\":\"lost\",\"id\":\"r4\",\"reason\":\"nothing arrived\"}"));
        Require(broken.Stage == TransferStage.Failed, "a lost link fails the row");
        Require(broken.Failure == "没有收到数据", "a lost link is explained in words");

        // ...but 'lost' is only ever a timeout guess ("no upload started in
        // time"), while 'stored' means the bytes really landed on disk. A
        // transfer that was called lost and then completed has to end up shown
        // as completed: the file is there, and leaving a red row over a file
        // the person can see in their folder is the worse lie.
        state.Apply(Event("{\"event\":\"stored\",\"id\":\"r4\"}"));
        Require(broken.Stage == TransferStage.Done, "a stored event settles a transfer that was called lost");

        // An event with no id cannot be attributed to any row.
        Require(state.Apply(Event("{\"event\":\"incoming\",\"done\":1}")) is null,
            "an event without an id is ignored");
        // Nor can a non-object.
        Require(state.Apply(Event("\"nonsense\"")) is null, "a non-object event is ignored");

        Console.WriteLine("PASS: transfer rows come from transfer_created and never claim success early");
    }
}
