using System.Text.Json;
using DropLink;

internal static class TransferStateChecks
{
    private static JsonElement Event(string text) => JsonDocument.Parse(text).RootElement.Clone();
    internal static void Run()
    {
        var state = new TransferState();
        if (state.Apply(Event("{\"event\":\"stored\",\"id\":\"r1\"}")) is not null) throw new Exception("unknown transfer accepted");
        var row = state.Apply(Event("{\"event\":\"asked\",\"id\":\"r1\",\"sender\":\"Phone\",\"waits\":true,\"files\":[{\"name\":\"a.jpg\"}]}")) ?? throw new Exception("request missing");
        if (state.Apply(Event("{\"event\":\"stored\",\"id\":\"r1\"}")) is not null || row.Status == "已完成") throw new Exception("premature success");
        state.Apply(Event("{\"event\":\"answered\",\"id\":\"r1\",\"how\":\"accepted\"}"));
        state.Apply(Event("{\"event\":\"incoming\",\"id\":\"r1\",\"done\":50,\"total\":100}"));
        if (row.Progress != "50%") throw new Exception("wrong progress");
        if (state.Apply(Event("{\"event\":\"answered\",\"id\":\"r1\",\"how\":\"accepted\"}")) is not null || row.Status != "正在接收") throw new Exception("duplicate answer rolled back active transfer");
        if (state.Apply(Event("{\"event\":\"answered\",\"id\":\"r1\",\"how\":\"rejected\"}")) is not null || row.Finished) throw new Exception("conflicting answer changed accepted transfer");
        state.Apply(Event("{\"event\":\"lost\",\"id\":\"r1\"}"));
        state.Apply(Event("{\"event\":\"stored\",\"id\":\"r1\"}"));
        if (row.Status != "接收失败") throw new Exception("failed transfer displayed as success");
        var another = state.Apply(Event("{\"event\":\"asked\",\"id\":\"r2\",\"waits\":true}"))!;
        state.Apply(Event("{\"event\":\"answered\",\"id\":\"r2\",\"how\":\"accepted\"}"));
        state.Apply(Event("{\"event\":\"incoming\",\"id\":\"r2\",\"done\":1}"));
        state.Apply(Event("{\"event\":\"stored\",\"id\":\"r2\"}"));
        if (another.Status != "已完成") throw new Exception("stored transfer not completed");
        Console.WriteLine("PASS: transfer events require confirmation and success follows completion");
    }
}
