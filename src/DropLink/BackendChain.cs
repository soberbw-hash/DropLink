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
        UsbDeviceSnapshot? selectedTarget = null, bool allowLegacySingleDevice = false)
    {
        await lifecycle.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (started || processes.Count != 0) throw new InvalidOperationException("链路已启动。");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("无线电后端需要 Windows。");
            var check = RadioPreflight.Check(devices, selectedInstance, authorized, recoveryVerified,
                [files.Radio, files.Bridge, files.Netd, files.Awdld]);
            if (!check.Ready) throw new InvalidOperationException(string.Join(" ", check.Problems));
            // Two ways to pin the device:
            //  1. strict: a libusb topology target resolved from the selected PnP
            //     instance (needs a backend with --list-devices).
            //  2. legacy: the bundled backend predates --list-devices, so the
            //     radio opens by VID/PID. RadioPreflight already proved that
            //     exactly one supported card is present and it is the selected
            //     one, so a VID/PID open cannot pick a different adapter.
            bool strict = ValidatedTargetSelection(selectedTarget);
            if (!strict && !(allowLegacySingleDevice && selectedTarget is null))
                throw new NotSupportedException("无法唯一定位无线网卡；接收已禁用。");
            radioStop = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\DropBridgeRadioStop", out bool created);
            if (!created) throw new InvalidOperationException("另一接收链路正在运行或停止事件已存在。");
            sessionDirectory = Path.Combine(Path.GetTempPath(), "DropLink-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDirectory);
            // The radio spends ~45 s on firmware + BB/RF init before it serves
            // its TCP port, so the budget is generous and readiness is also
            // confirmed by probing the port (see StartStage).
            await StartStage(files.Radio, ["149", "20"], BuildRadioEnvironment(selectedTarget),
                "listening on 127.0.0.1:49876", TimeSpan.FromSeconds(150), cancel,
                RadioListeningAsync).ConfigureAwait(false);
            await StartStage(files.Bridge, [], [], "waiting for awdld", TimeSpan.FromSeconds(30), cancel).ConfigureAwait(false);
            // netd writes the local AWDL address into AWDL_NETD_ADDR_FILE and
            // proxies mDNS/DNS-SD. The engine then talks to netd instead of
            // binding an adapter — there is no awdl0 NIC on Windows (awdld only
            // reports that name), so a direct bind can never succeed.
            var addressFile = Path.Combine(sessionDirectory, "awdl_netd.addr");
            await StartStage(files.Netd, [], new Dictionary<string, string?> {
                ["AWDL_NETD_MDNS_PROXY"] = "1",
                ["AWDL_NETD_ADDR_FILE"] = addressFile
            }, "waiting for awdld", TimeSpan.FromSeconds(30), cancel).ConfigureAwait(false);
            // awdld speaks to the radio over \\.\pipe\winawdl-radio and requires
            // a protocol-3 greeting. It can connect a moment before the greeting
            // arrives and then abort, so start it with a short retry loop rather
            // than guessing a sleep duration.
            var awdldArgs = new[] { "-c", "149", "-s", "1", "-j", "400", "-v" };
            var awdldEnv = new Dictionary<string, string?> {
                ["AWDL_ELECTION_METRIC"] = "200", ["AWDL_ELECTION_COUNTER"] = "100000",
                ["AWDL_CHANSEQ"] = "social", ["AWDL_FOLLOW_MASTER_SEQ"] = null
            };
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await StartStage(files.Awdld, awdldArgs, awdldEnv, "host interface",
                        TimeSpan.FromSeconds(60), cancel).ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (attempt < 4)
                {
                    processes.RemoveAll(p => { try { return p.HasExited; } catch { return true; } });
                    cancel.ThrowIfCancellationRequested();
                    await Task.Delay(TimeSpan.FromSeconds(3), cancel).ConfigureAwait(false);
                }
            }
            // netd publishes the address file once awdld has brought the link
            // up; starting the engine before that makes it wait on nothing.
            var addressDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (true)
            {
                if (processes.Any(p => { try { return p.HasExited; } catch { return true; } }))
                    throw new InvalidOperationException("后端在初始化期间退出。");
                try
                {
                    if (File.Exists(addressFile) &&
                        IPAddress.TryParse(File.ReadAllText(addressFile).Trim(), out var parsed) &&
                        parsed.AddressFamily == AddressFamily.InterNetworkV6 && parsed.IsIPv6LinkLocal)
                        break;
                }
                catch (IOException) { /* still being written */ }
                if (DateTime.UtcNow >= addressDeadline)
                    throw new TimeoutException("awdl_netd 未在 60 秒内写出 AWDL 地址。");
                cancel.ThrowIfCancellationRequested();
                await Task.Delay(300, cancel).ConfigureAwait(false);
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

    // Port probe used as an independent readiness signal for the radio stage.
    // Connecting proves the backend is accepting clients; nothing is sent.
    internal static async Task<bool> RadioListeningAsync()
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            await client.ConnectAsync(IPAddress.Loopback, 49876, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch { return false; }
    }

    // Fail-closed. Supplies strict topology to the radio ONLY when a resolved
    // target is passed in; default behavior remains the legacy VID/PID open.
    // Kept internal+static so this mapping is unit-testable without starting
    // any process or touching hardware.
    internal static Dictionary<string, string?> BuildRadioEnvironment(UsbDeviceSnapshot? target)
    {
        // LOG_LEVEL stays at "warn" (the value the reference chain script uses):
        // "listening on 127.0.0.1:49876" is printed regardless of level, and
        // keeping INFO would emit thousands of bulk_send lines during RF init.
        var env = new Dictionary<string, string?> {
            ["AIRBRIDGE_VID"] = "0x0bda", ["AIRBRIDGE_PID"] = "0xc811",
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

    // Safety gate — no longer an unconditional false. It opens ONLY for a
    // target whose identity came from a REAL libusb enumeration (never
    // fabricated) and passed PnP preflight upstream: supported VID/PID, a
    // physical bus number, and a well-formed dotted port path. With no target,
    // or a synthetic/unsupported one, it still fails closed. Note this does
    // not itself bind a driver or open a device — that is the radio's job and
    // is gated by RadioPreflight.Check (WinUSB, authorization, recovery).
    internal static bool ValidatedTargetSelection(UsbDeviceSnapshot? target)
    {
        if (target is null) return false;
        if (!RadioPreflight.IsSupportedUsbId(target.Vid, target.Pid)) return false;
        if (target.Bus <= 0) return false;
        var port = target.PortPath;
        if (string.IsNullOrWhiteSpace(port)) return false;
        foreach (var segment in port.Split('.'))
            if (!int.TryParse(segment, out var n) || n < 0) return false;
        return true;
    }

    private async Task StartStage(string path, string[] args, Dictionary<string, string?> environment,
        string ready, TimeSpan timeout, CancellationToken cancel, Func<Task<bool>>? probe = null)
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
        // Readiness is accepted from either the log line or a live probe. The
        // probe exists because the log wording/level is not contractual: the
        // radio only emits "listening on ..." after a ~45 s RF init, and a
        // changed log level would otherwise look like a hang.
        var deadline = DateTime.UtcNow + timeout;
        while (!readySignal.Task.IsCompleted)
        {
            if (probe is not null)
            {
                bool ok = false;
                try { ok = await probe().ConfigureAwait(false); } catch { /* not listening yet */ }
                if (ok) { readySignal.TrySetResult(); break; }
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"后端未在 {timeout.TotalSeconds:0} 秒内就绪：{Path.GetFileName(path)}");
            cancel.ThrowIfCancellationRequested();
            await Task.Delay(400, cancel).ConfigureAwait(false);
        }
        await readySignal.Task.ConfigureAwait(false);
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
