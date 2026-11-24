using System.Text.Json;
using DropLink;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var mock = Path.Combine(root, "tests/EngineClientChecks/mock_engine.py");
var python = Environment.GetEnvironmentVariable("DROPLINK_TEST_PYTHON") ?? "/usr/bin/python3";
var asked = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
var answered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
var unexpectedExit = false;
var temp = Path.Combine(Path.GetTempPath(), "droplink-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    RadioPreflightChecks.Run(temp);
    TransferStateChecks.Run();
    if (!OperatingSystem.IsWindows())
    {
        await using var chain = new BackendChain();
        try
        {
            await chain.StartAsync(new ChainFiles("x", "x", "x", "x"), [], "", false, false, CancellationToken.None);
            throw new Exception("chain should not run on Linux");
        }
        catch (PlatformNotSupportedException) { Console.WriteLine("PASS: Windows-only chain fails closed on Linux"); }
    }
    await using (var client = EngineClient.Start(python, mock, Path.Combine(temp, "address"), temp, temp,
        "测试 device", evt =>
        {
            if (evt.GetProperty("event").GetString() == "asked") asked.TrySetResult(evt);
            if (evt.GetProperty("event").GetString() == "answered") answered.TrySetResult(evt);
        }, _ => unexpectedExit = true))
    {
        var request = await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (request.GetProperty("id").GetString() != "r1") throw new Exception("wrong request");
        await client.AnswerAsync("r1", false);
        var response = await answered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (response.GetProperty("accept").GetBoolean()) throw new Exception("decline was not preserved");
    }
    if (unexpectedExit) throw new Exception("normal shutdown reported as crash");
    Console.WriteLine("PASS: malformed events ignored, request delivered, decline preserved, clean shutdown");
    var ready = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    var actual = Path.Combine(root, ".prepared/winawdl/airdrop/engine.py");
    await using (var client = EngineClient.Start(python, actual, Path.Combine(temp, "missing-address"), temp,
        Path.Combine(temp, "keys"), "DropLink test", evt =>
        {
            if (evt.GetProperty("event").GetString() == "ready") ready.TrySetResult(evt);
        }, code => ready.TrySetException(new Exception($"engine exited: {code}"))))
    {
        var eventData = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (!eventData.GetProperty("ask").GetBoolean()) throw new Exception("confirmation disabled");
    }
    Console.WriteLine("PASS: actual patched engine starts with confirmation and exits cleanly");
}
finally { Directory.Delete(temp, true); }
