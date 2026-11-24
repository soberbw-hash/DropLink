using System.Text.Json;
using DropLink;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var mock = Path.Combine(root, "tests/EngineClientChecks/mock_engine.py");
// Resolve an interpreter that actually exists on this host. The previous
// hardcoded /usr/bin/python3 made the whole suite fail on Windows with a
// FileNotFoundException that looked like a product bug. Order: explicit
// override, then the bundled portable runtime, then whatever is on PATH.
var python = ResolvePython();
static string ResolvePython()
{
    var overridden = Environment.GetEnvironmentVariable("DROPLINK_TEST_PYTHON");
    if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

    if (OperatingSystem.IsWindows())
    {
        var bundled = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "DropLink-可运行", "backend", "python", "python.exe");
        if (File.Exists(bundled)) return bundled;
        return "python.exe";
    }
    return File.Exists("/usr/bin/python3") ? "/usr/bin/python3" : "python3";
}
var asked = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
var answered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
var unexpectedExit = false;
var temp = Path.Combine(Path.GetTempPath(), "droplink-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    // Build an isolated self-contained layout so path bootstrap checks do not
    // depend on a prepared-next tree being present in the source checkout.
    var work = Path.Combine(temp, "work");
    var realExeDir = Path.Combine(work, "DropLink", "src", "DropLink", "bin", "Release", "net10.0-windows");
    var prepared = Path.Combine(work, "prepared-next");
    var radioDir = Path.Combine(prepared, "dropbridge", "build", "radio-local2", "Release");
    var netdDir = Path.Combine(prepared, "dropbridge", "build", "netd-local", "Release");
    var awdlDir = Path.Combine(prepared, "winawdl", "build", "awdl-portable");
    var backupDir = Path.Combine(work, "_backup", "droplink-driver-20261124");
    foreach (var dir in new[] { radioDir, netdDir, awdlDir, backupDir, realExeDir })
        Directory.CreateDirectory(dir);
    foreach (var file in new[]
    {
        Path.Combine(radioDir, "airbridge_radio.exe"),
        Path.Combine(radioDir, "radio_pipe_bridge.exe"),
        Path.Combine(netdDir, "awdl_netd.exe"),
        Path.Combine(awdlDir, "awdld.exe"),
        Path.Combine(backupDir, "一键换WinUSB.cmd"),
        Path.Combine(backupDir, "回退驱动.cmd")
    }) File.WriteAllText(file, "placeholder");
    AppPathsChecks.Run(realExeDir);
    RadioPreflightChecks.Run(temp);
    ReadinessReportChecks.Run(temp);
    SetupGuideChecks.Run(temp);
    UsbTopologyChecks.Run();
    ProcessJobChecks.Run();
    if (OperatingSystem.IsWindows()) SingleInstanceChecks.Run();
    else Console.WriteLine("SKIP: single instance is Windows-only");
    await ReceiverSessionChecks.Run(temp);
    TransferStateChecks.Run();
CrashBudgetChecks.Run();
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
    if (args.Contains("--mock-only"))
    {
        Console.WriteLine("SKIP: real patched engine was not tested (--mock-only)");
        return;
    }
    var ready = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
    var engineDir = Environment.GetEnvironmentVariable("DROPLINK_AIRDROP_DIR")
        ?? Path.Combine(root, ".prepared/winawdl/airdrop");
    var actual = Path.Combine(engineDir, "engine.py");
    if (!File.Exists(actual))
    {
        Console.WriteLine("SKIP: patched engine not found; run tools/prepare-sources.py --airdrop-only "
            + "or point DROPLINK_AIRDROP_DIR at an assembled backend/airdrop folder");
        return;
    }
    await using (var client = EngineClient.Start(python, actual, Path.Combine(temp, "missing-address"), temp,
        Path.Combine(temp, "keys"), "DropLink test", evt =>
        {
            if (evt.GetProperty("event").GetString() == "ready") ready.TrySetResult(evt);
        }, code => ready.TrySetException(new Exception($"engine exited: {code}"))))
    {
        var eventData = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (!eventData.TryGetProperty("protocol", out _)) throw new Exception("engine did not report a protocol");
        // An ambient data/settings.json may enable auto-receive, which turns the
        // confirmation flag off by design; only assert it when it is expected.
        var asking = eventData.TryGetProperty("ask", out var askFlag) && askFlag.ValueKind == JsonValueKind.True;
        if (asking)
            Console.WriteLine("PASS: actual patched engine starts with confirmation and exits cleanly");
        else
            Console.WriteLine("PASS: actual patched engine starts and exits cleanly "
                + "(confirmation disabled by ambient settings.json)");
    }
}
finally { Directory.Delete(temp, true); }
