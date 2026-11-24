using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace DropLink;

// Experimental chain controller. Not yet connected to the UI or approved for real hardware.
internal sealed record ChainFiles(string Radio, string Bridge, string Netd, string Awdld);

internal sealed class BackendChain : IAsyncDisposable
{
    private readonly List<Process> processes = [];
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private EventWaitHandle? radioStop;
    private int stopping;
    private bool started;
    private string? sessionDirectory;
    public event Action<string>? Failed;

    // The caller must obtain current read-only device data, explicit authorization and recovery evidence.
    // This method must never be wired to an automatic startup or driver installation path.
    internal async Task<string> StartAsync(ChainFiles files, IReadOnlyList<RadioDevice> devices,
        string selectedInstance, bool authorized, bool recoveryVerified, CancellationToken cancel,
        UsbDeviceSnapshot? selectedTarget = null)
    {
        await lifecycle.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (started || processes.Count != 0) throw new InvalidOperationException("链路已启动。");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("无线电后端需要 Windows。");
            var check = RadioPreflight.Check(devices, selectedInstance, authorized, recoveryVerified,
                [files.Radio, files.Bridge, files.Netd, files.Awdld]);
            if (!check.Ready) throw new InvalidOperationException(string.Join(" ", check.Problems));
            // The upstream radio opened by VID/PID (first match); the pinned
            // patch now accepts AIRBRIDGE_USB_BUS/PORT for strict topology. But
            // a topology value is only safe if it was resolved from the user's
            // selected PnP instance and verified on real hardware — neither is
            // proven here, so the safety lock stays closed. This method NEVER
            // fabricates a bus/port or substitutes a VID/PID-only open.
            if (!ValidatedTargetSelection(selectedTarget))
                throw new NotSupportedException("无线后端尚不能安全地精确选择目标设备；接收已禁用。");
            radioStop = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\DropBridgeRadioStop", out bool created);
            if (!created) throw new InvalidOperationException("另一接收链路正在运行或停止事件已存在。");
            sessionDirectory = Path.Combine(Path.GetTempPath(), "DropLink-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDirectory);
            string addressFile = Path.Combine(sessionDirectory, "awdl.addr");
            await StartStage(files.Radio, ["149", "20"], BuildRadioEnvironment(selectedTarget),
                "listening on 127.0.0.1:49876", TimeSpan.FromSeconds(120), cancel).ConfigureAwait(false);
            await StartStage(files.Bridge, [], [], "waiting for awdld", TimeSpan.FromSeconds(30), cancel).ConfigureAwait(false);
            await StartStage(files.Netd, [], new Dictionary<string, string?> {
                ["AWDL_NETD_MDNS_PROXY"] = "1", ["AWDL_NETD_ADDR_FILE"] = addressFile
            }, "waiting for awdld", TimeSpan.FromSeconds(30), cancel).ConfigureAwait(false);
            await StartStage(files.Awdld, ["-c", "149", "-s", "1", "-j", "400", "-v"], new Dictionary<string, string?> {
                ["AWDL_ELECTION_METRIC"] = "200", ["AWDL_ELECTION_COUNTER"] = "100000",
                ["AWDL_CHANSEQ"] = "social", ["AWDL_FOLLOW_MASTER_SEQ"] = null
            }, "host interface", TimeSpan.FromSeconds(60), cancel).ConfigureAwait(false);
            using var addressTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            addressTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                addressTimeout.Token.ThrowIfCancellationRequested();
                if (processes.Any(p => p.HasExited)) throw new InvalidOperationException("后端在初始化期间退出。");
                try
                {
                    var text = (await File.ReadAllTextAsync(addressFile, addressTimeout.Token).ConfigureAwait(false)).Trim();
                    if (IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal) break;
                }
                catch (IOException) { }
                await Task.Delay(250, addressTimeout.Token).ConfigureAwait(false);
            }
            started = true;
            return addressFile;
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    // Fail-closed. Supplies strict topology to the radio ONLY when a resolved
    // target is passed in; default behavior remains the legacy VID/PID open.
    // Kept internal+static so this mapping is unit-testable without starting
    // any process or touching hardware.
    internal static Dictionary<string, string?> BuildRadioEnvironment(UsbDeviceSnapshot? target)
    {
        var env = new Dictionary<string, string?> {
            ["AIRBRIDGE_VID"] = "0x0bda", ["AIRBRIDGE_PID"] = "0xc820",
            ["AIRBRIDGE_LOG_LEVEL"] = "warn", ["AIRBRIDGE_LINK_STATS"] = "1",
            ["AIRBRIDGE_FASTRETUNE_FW"] = "2", ["AIRBRIDGE_PARENT_PID"] = Environment.ProcessId.ToString()
        };
        if (target is not null)
        {
            env["AIRBRIDGE_VID"] = $"0x{target.Vid:x4}";
            env["AIRBRIDGE_PID"] = $"0x{target.Pid:x4}";
            env["AIRBRIDGE_USB_BUS"] = target.Bus.ToString();
            if (!string.IsNullOrWhiteSpace(target.PortPath)) env["AIRBRIDGE_USB_PORT"] = target.PortPath;
        }
        return env;
    }

    // Safety lock. Stays false until precise PnP<->libusb binding and recovery
    // are proven on real hardware; passing a target does not open it.
    private static bool ValidatedTargetSelection(UsbDeviceSnapshot? target) => false;

    private async Task StartStage(string path, string[] args, Dictionary<string, string?> environment,
        string ready, TimeSpan timeout, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path))! };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var entry in environment)
            if (entry.Value is null) info.Environment.Remove(entry.Key); else info.Environment[entry.Key] = entry.Value;
        var readySignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new Process { StartInfo = info, EnableRaisingEvents = true };
        void Line(string? line) { if (line is not null && line.Contains(ready, StringComparison.Ordinal)) readySignal.TrySetResult(); }
        child.OutputDataReceived += (_, e) => Line(e.Data);
        child.ErrorDataReceived += (_, e) => Line(e.Data);
        child.Exited += (_, _) =>
        {
            readySignal.TrySetException(new InvalidOperationException("后端进程提前退出。"));
            if (Volatile.Read(ref stopping) == 0 && started)
            {
                Failed?.Invoke("后端进程意外退出，链路正在关闭。");
                _ = StopAsync();
            }
        };
        if (!child.Start()) { child.Dispose(); throw new InvalidOperationException("无法启动后端。"); }
        processes.Add(child);
        child.BeginOutputReadLine(); child.BeginErrorReadLine();
        await readySignal.Task.WaitAsync(timeout, cancel).ConfigureAwait(false);
        if (child.HasExited) throw new InvalidOperationException("后端就绪后立即退出。");
    }

    internal async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        try
        {
            for (int i = processes.Count - 1; i >= 0; i--)
            {
                var child = processes[i];
                try
                {
                    if (!child.HasExited)
                    {
                        if (i == 0 && radioStop is not null)
                        {
                            radioStop.Set();
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                            try { await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) { if (!child.HasExited) child.Kill(true); }
                        }
                        else child.Kill(true);
                        await child.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                finally { child.Dispose(); }
            }
            processes.Clear();
            radioStop?.Dispose(); radioStop = null;
            if (sessionDirectory is not null)
            {
                try { Directory.Delete(sessionDirectory, true); } catch (IOException) { }
                sessionDirectory = null;
            }
            started = false;
        }
        finally { Interlocked.Exchange(ref stopping, 0); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
