using System.Text.Json;

namespace DropLink;

// Orchestrates the wireless chain and the confirmation engine as ONE lifecycle.
// It never starts anything on its own: StartAsync still goes through
// BackendChain, whose ValidatedTargetSelection() safety lock stays closed until
// precise device selection and recovery are proven. The WPF receive button also
// stays disabled. This class only wires the pieces so a future validated entry
// point has a single, testable, reverse-order-cleanup path to call.
internal sealed class ReceiverSession : IAsyncDisposable
{
    private readonly BackendChain chain = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private EngineClient? engine;
    private bool started;
    private int stopping;
    private int disposed;
    // Everything needed to start the engine again without touching the radio.
    private string? addressFile;
    private EngineLaunch? launch;
    // Crash timestamps inside a rolling window. Three crashes in ten minutes
    // means the chain is broken, not that one process had a bad day, so only
    // then is a 40-60 s full rebuild justified.
    private readonly CrashBudget crashes = new(CrashBudgetSize, CrashWindowMs);
    private const int CrashBudgetSize = 3;
    private const long CrashWindowMs = 10 * 60 * 1000;

    private sealed record EngineLaunch(string Python, string Script, string Output, string Keys, string Name);

    public event Action<JsonElement>? EngineEvent;
    public event Action<string>? Failed;
    // Forwarded from the chain so the UI can show which stage is running.
    public event Action<string>? Stage;
    // Recoverable events: reported, but they do not tear the session down.
    public event Action<string>? Notice;

    internal async Task StartAsync(
        ChainFiles files, IReadOnlyList<RadioDevice> devices, string selectedInstance,
        bool authorized, bool recoveryVerified,
        string python, string engineScript, string outputDirectory, string keysDirectory, string deviceName,
        UsbDeviceSnapshot? selectedTarget = null, bool allowLegacySingleDevice = false,
        CancellationToken cancel = default)
    {
        await lifecycle.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (started || engine is not null) throw new InvalidOperationException("接收会话已启动。");
            chain.Failed += OnChainFailed;
            chain.Stage += OnChainStage;
            // The device gate lives in BackendChain.StartAsync; it throws before
            // any engine process is created when no target can be proven.
            addressFile = await chain.StartAsync(
                files, devices, selectedInstance, authorized, recoveryVerified, cancel, selectedTarget,
                allowLegacySingleDevice).ConfigureAwait(false);
            launch = new EngineLaunch(python, engineScript, outputDirectory, keysDirectory, deviceName);
            crashes.Reset();
            try
            {
                engine = StartEngine();
                started = true;
            }
            catch
            {
                await chain.StopAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    private EngineClient StartEngine()
    {
        var plan = launch ?? throw new InvalidOperationException("接收会话缺少引擎参数。");
        return EngineClient.Start(plan.Python, plan.Script, addressFile ?? "", plan.Output,
            plan.Keys, plan.Name, evt => EngineEvent?.Invoke(evt), OnEngineExit);
    }

    // A dead engine does not mean a dead radio. Restarting only the engine costs
    // about a second; rebuilding the chain costs 40-60 s of firmware and RF init.
    // Only when that fails does the session fall back to a full teardown.
    private void OnEngineExit(int code)
    {
        if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref disposed) != 0) return;
        // Detach right away. The crashed client still owns the pipes and the
        // process handle, and leaving it in `engine` would make the retry loop
        // below mistake "old dead client" for "already restarted".
        var dead = Interlocked.Exchange(ref engine, null);
        if (dead is not null) _ = DisposeQuietlyAsync(dead);
        _ = RecoverEngineAsync(code);
    }

    private static async Task DisposeQuietlyAsync(EngineClient client)
    {
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { AppLog.Write("engine dispose failed: " + error.Message); }
    }

    private bool OverCrashBudget() => crashes.Record();

    private async Task RecoverEngineAsync(int code)
    {
        // Repeated crashes point at a broken chain, not a one-off exit: stop
        // pretending and rebuild from the radio up.
        if (OverCrashBudget())
        {
            Failed?.Invoke($"接收引擎在 10 分钟内反复退出（代码 {code}），正在重建无线链路。");
            await StopQuietlyAsync().ConfigureAwait(false);
            return;
        }
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref disposed) != 0) return;
            Notice?.Invoke($"接收引擎退出（代码 {code}），正在重启引擎（第 {attempt} 次）…");
            try { await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 1 : 3)).ConfigureAwait(false); }
            catch (Exception) { return; }

            await lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref disposed) != 0) return;
                if (engine is not null) return;
                engine = StartEngine();
                AppLog.Mark($"engine:restarted (attempt {attempt})");
                Notice?.Invoke("接收引擎已恢复，无需重新初始化网卡。");
                return;
            }
            catch (Exception error)
            {
                AppLog.Write($"engine restart {attempt} failed: {error.Message}");
            }
            finally { lifecycle.Release(); }
        }
        // The engine cannot come back on its own; rebuild everything.
        Failed?.Invoke($"接收引擎反复退出（代码 {code}），正在重建无线链路。");
        await StopQuietlyAsync().ConfigureAwait(false);
    }

    internal Task AnswerAsync(string id, bool accept)
    {
        var current = engine ?? throw new InvalidOperationException("接收会话未启动。");
        return current.AnswerAsync(id, accept);
    }

    private void OnChainFailed(string message) => Failed?.Invoke(message);

    private void OnChainStage(string message) => Stage?.Invoke(message);

    private async Task StopQuietlyAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        catch { /* cleanup failures are reported through Failed, never thrown here */ }
    }

    internal async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        // Tells the engine exit callback that this teardown is intentional, so it
        // must not try to restart anything while we are shutting down.
        Interlocked.Exchange(ref stopping, 1);
        try
        {
            // Reverse order: stop the engine (and its IPC readers) before tearing
            // down the radio chain it depends on. Both steps are idempotent.
            var current = engine;
            engine = null;
            if (current is not null)
            {
                try { await current.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { Failed?.Invoke("接收引擎清理失败：" + error.Message); }
            }
            await chain.StopAsync().ConfigureAwait(false);
            started = false;
            addressFile = null;
        }
        finally { Interlocked.Exchange(ref stopping, 0); }
    }

    public async ValueTask DisposeAsync()
    {
        // Permanent: a crash callback that lands after this point must never be
        // able to restart an engine on a session the UI has already replaced.
        Interlocked.Exchange(ref disposed, 1);
        chain.Failed -= OnChainFailed;
        chain.Stage -= OnChainStage;
        await StopAsync().ConfigureAwait(false);
        await chain.DisposeAsync().ConfigureAwait(false);
    }
}
