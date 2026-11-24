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

    public event Action<JsonElement>? EngineEvent;
    public event Action<string>? Failed;

    internal async Task StartAsync(
        ChainFiles files, IReadOnlyList<RadioDevice> devices, string selectedInstance,
        bool authorized, bool recoveryVerified,
        string python, string engineScript, string outputDirectory, string keysDirectory, string deviceName,
        CancellationToken cancel = default)
    {
        await lifecycle.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (started || engine is not null) throw new InvalidOperationException("接收会话已启动。");
            chain.Failed += OnChainFailed;
            // Safety lock lives in BackendChain.StartAsync. With it closed this
            // throws before any engine process is created.
            var addressFile = await chain.StartAsync(
                files, devices, selectedInstance, authorized, recoveryVerified, cancel).ConfigureAwait(false);
            try
            {
                engine = EngineClient.Start(python, engineScript, addressFile, outputDirectory,
                    keysDirectory, deviceName, evt => EngineEvent?.Invoke(evt), code =>
                    {
                        Failed?.Invoke($"接收引擎意外退出（代码 {code}）。");
                        // Fire-and-forget: serialized by lifecycle; normal disposal
                        // suppresses the engine's exit callback, so no recursion.
                        _ = StopQuietlyAsync();
                    });
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

    internal Task AnswerAsync(string id, bool accept)
    {
        var current = engine ?? throw new InvalidOperationException("接收会话未启动。");
        return current.AnswerAsync(id, accept);
    }

    private void OnChainFailed(string message) => Failed?.Invoke(message);

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
    }

    public async ValueTask DisposeAsync()
    {
        chain.Failed -= OnChainFailed;
        await StopAsync().ConfigureAwait(false);
        await chain.DisposeAsync().ConfigureAwait(false);
    }
}
