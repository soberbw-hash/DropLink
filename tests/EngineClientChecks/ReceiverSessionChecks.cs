using DropLink;

internal static class ReceiverSessionChecks
{
    internal static async Task Run(string temp)
    {
        var files = new ChainFiles("radio", "bridge", "netd", "awdld");
        // Program.cs resolves the interpreter once and exports it. A hardcoded
        // path here used to point at the build machine's home directory, which
        // is both a portability bug and somebody's user name in a public repo.
        var python = Environment.GetEnvironmentVariable("DROPLINK_TEST_PYTHON")
                     ?? "python.exe";
        var script = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")),
            "tests/EngineClientChecks/mock_engine.py");

        await using var session = new ReceiverSession();
        var events = 0;
        session.EngineEvent += _ => events++;

        // Safety lock closed / no authorized validated device: StartAsync must
        // refuse before creating any engine process, and no event may flow.
        try
        {
            await session.StartAsync(files, [], "", false, false,
                python, script, temp, temp, "test device", cancel: CancellationToken.None);
            throw new Exception("ReceiverSession started without validated authorization");
        }
        catch (NotSupportedException) { /* expected: ValidatedTargetSelection() is closed */ }
        catch (InvalidOperationException) { /* preflight refusal is also acceptable */ }
        if (events != 0) throw new Exception("engine event emitted despite failed start");

        // Stopping a never-started session is a safe no-op (idempotent cleanup).
        await session.StopAsync();
        await session.StopAsync();

        Console.WriteLine("PASS: receiver session fails closed, no engine event, cleanup is idempotent");
    }
}
