using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DropLink;

// Experimental IPC client only. It does not start the radio or install drivers.
// The UI must not enable receiving until the entire chain is validated.
internal sealed class EngineClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task eventReader;
    private readonly Task errorReader;
    private readonly Task exitObserver;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private int stopping;

    private readonly Action<JsonElement> eventReceived;
    private readonly Action<int> exited;

    private EngineClient(Process process, Action<JsonElement> eventReceived, Action<int> exited)
    {
        this.process = process;
        this.eventReceived = eventReceived;
        this.exited = exited;
        eventReader = ReadEventsAsync();
        errorReader = DrainErrorsAsync();
        exitObserver = ObserveExitAsync();
    }

    public static EngineClient Start(string python, string engine, string addressFile,
        string outputDirectory, string keysDirectory, string deviceName,
        Action<JsonElement> eventReceived, Action<int> exited)
    {
        foreach (var file in new[] { python, engine })
            if (!File.Exists(file)) throw new FileNotFoundException("Backend executable or script is missing.", file);
        if (!Directory.Exists(outputDirectory)) throw new DirectoryNotFoundException("Receive directory does not exist.");
        if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentException("Device name is required.", nameof(deviceName));
        var info = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(engine))!
        };
        var arguments = new List<string> { "-u", Path.GetFullPath(engine) };
        // Proxy mode (engine talks to awdl_netd over loopback) is used when the
        // chain produced an address file. Bundled backends that create the awdl0
        // adapter themselves need the engine to bind that adapter directly.
        if (!string.IsNullOrWhiteSpace(addressFile))
        {
            arguments.Add("--proxy-address-file");
            arguments.Add(Path.GetFullPath(addressFile));
        }
        foreach (string arg in new[] { "--outdir", Path.GetFullPath(outputDirectory), "--keys", Path.GetFullPath(keysDirectory), "--name", deviceName })
            arguments.Add(arg);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var child = Process.Start(info) ?? throw new InvalidOperationException("Could not start receiver engine.");
        // Keep the engine inside the host's job so it can never outlive us.
        ProcessJob.Assign(child);
        AppLog.Mark("engine:process started (pid " + child.Id + ")");
        return new EngineClient(child, eventReceived, exited);
    }

    public Task AnswerAsync(string id, bool accept) => SendAsync(new { @do = "answer", id, accept });

    /// <summary>
    /// Rename the receiver while it is running. The engine validates the name
    /// and reports a refusal through its 'settings' event; nothing here can
    /// decide whether a name is acceptable.
    /// </summary>
    public Task SetNameAsync(string name) => SendAsync(new { @do = "set", name });

    /// <summary>
    /// Stops accepting new requests without tearing the radio down. Rebuilding
    /// the chain costs 40-60 s, so "暂停接收" that stopped the session would make
    /// resuming feel broken; the engine simply declines what arrives.
    /// </summary>
    public Task SetPausedAsync(bool paused) => SendAsync(new { @do = "pause", on = paused });

    private async Task SendAsync(object command)
    {
        await writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref stopping) != 0 || process.HasExited)
                throw new InvalidOperationException("Receiver engine is not running.");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    private async Task ReadEventsAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    if (json.RootElement.ValueKind == JsonValueKind.Object &&
                        json.RootElement.TryGetProperty("event", out var value) && value.ValueKind == JsonValueKind.String)
                        eventReceived(json.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // Never treated as a success event, but never silently lost
                    // either: an unparsable line is usually a Python traceback
                    // that landed on stdout instead of stderr.
                    AppLog.WriteEngineStderr("ipc: unparsable stdout line: " + Truncate(line));
                }
            }
        }
        catch (IOException) { }
    }

    private async Task DrainErrorsAsync()
    {
        // stderr used to be consumed and thrown away, which is why a failed
        // start only ever surfaced as one opaque sentence. Persist it instead.
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { Length: > 0 } line)
                AppLog.WriteEngineStderr(line);
        }
        catch (IOException) { }
    }

    private static string Truncate(string line) =>
        line.Length <= 500 ? line : line[..500] + "…";

    private async Task ObserveExitAsync()
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        await eventReader.ConfigureAwait(false);
        if (Volatile.Read(ref stopping) == 0) exited(process.ExitCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        await writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("{\"do\":\"quit\"}").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    process.StandardInput.Close();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(eventReader, errorReader, exitObserver).ConfigureAwait(false);
        }
        finally { writeLock.Release(); process.Dispose(); }
    }
}
