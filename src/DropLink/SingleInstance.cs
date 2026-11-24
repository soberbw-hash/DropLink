using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace DropLink;

/// <summary>
/// One running DropLink per user session. A second launch signals the first
/// instance to show its window instead of starting another receiver.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\DropLink.SingleInstance";
    private const string ActivateEventName = @"Local\DropLink.Activate";
    private const string SettingsEventName = @"Local\DropLink.OpenSettings";
    // Set by the owner from its UI thread once it has actually put a window on
    // screen. Deliberately not set by the listener thread: a receiver whose UI
    // thread is wedged can still drain its named event, and answering "yes, I am
    // here" from a thread that cannot draw anything is exactly the lie that
    // leaves the user with an icon that does nothing.
    private const string AckEventName = @"Local\DropLink.Ack";

    // How long a second launch waits for the owner to prove it can still show a
    // window before it decides the owner is wedged and takes the lock away.
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(8);
    // Shorter, because there is no handshake to wait for: an owner from before
    // the handshake existed either pops its window in about a second or is not
    // going to. Making the user stare at nothing for 8 s to learn that is rude.
    private static readonly TimeSpan WindowWatchTimeout = TimeSpan.FromSeconds(4);

    private readonly Mutex mutex;
    // Not readonly: taking the lock away from a wedged instance happens after
    // the constructor has already found it was not ours.
    private bool ownsMutex;
    private readonly EventWaitHandle? activate;
    private readonly EventWaitHandle? openSettings;
    private EventWaitHandle? ack;
    private readonly Thread? listener;
    private volatile bool disposed;

    /// <summary>This process owns the receiver and must run.</summary>
    public bool IsFirst { get; }

    /// <summary>
    /// Another instance held the lock, never answered, and could not be ended.
    /// The caller has to tell the user instead of exiting silently.
    /// </summary>
    public int UnresponsiveOwnerPid { get; }

    /// <param name="onActivate">Bring the window forward.</param>
    /// <param name="onOpenSettings">Bring the window forward and open settings.</param>
    /// <param name="requestOpenSettings">
    /// Set by the process that was started with --settings. When another instance
    /// already owns the receiver, the request travels on its own named event, so
    /// "打开设置" from a shortcut lands on the running app instead of doing nothing.
    /// </param>
    public SingleInstance(Action onActivate, Action? onOpenSettings = null,
        string? scope = null, bool requestOpenSettings = false)
    {
        var suffix = string.IsNullOrWhiteSpace(scope) ? "" : "." + scope;
        var mutexName = MutexName + suffix;
        var eventName = ActivateEventName + suffix;
        var settingsName = SettingsEventName + suffix;
        var ackName = AckEventName + suffix;

        mutex = new Mutex(initiallyOwned: true, mutexName, out var created);
        ownsMutex = created;
        if (!created)
        {
            // The lock exists but nobody owns it: the previous instance died
            // without releasing (crash, task kill, power loss). A zero timeout
            // takes it over immediately and reports an abandoned mutex, which is
            // not an error here - it is the recovery.
            try
            {
                if (mutex.WaitOne(0)) ownsMutex = true;
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }
            if (ownsMutex)
            {
                created = true;
                AppLog.Write("took over an abandoned single-instance lock");
            }
        }

        if (!created)
        {
            // Someone is running. Ask it to show itself, then check that it
            // really did: an instance that never answers would otherwise leave
            // every later double-click doing nothing at all, with no way out
            // short of Task Manager.
            Ask(eventName, settingsName, requestOpenSettings);
            var others = OurProcesses();
            var ownerPid = others.Count > 0 ? others[0].Id : 0;
            if (WaitForAnswer(ackName, ownerPid))
            {
                DisposeAll(others);
                IsFirst = false;
                return;
            }

            var taken = TryTakeOver(others, out var stubborn);
            DisposeAll(others);
            if (taken)
            {
                IsFirst = true;
                AppLog.Write("the running instance did not answer; took the lock from it");
            }
            else
            {
                IsFirst = false;
                UnresponsiveOwnerPid = stubborn;
                AppLog.Write("another instance holds the lock and will not answer (pid " + stubborn + ")");
                return;
            }
        }
        else
        {
            IsFirst = true;
        }

        // The owner writes its pid where any later launch can find it. The named
        // events say "someone is there"; the pid is what makes it possible to do
        // something about it when nobody is.
        WriteOwnerPid();
        activate = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        // Created here, not in the waiter: an event that exists proves the owner
        // knows the handshake, which is how a launch tells "new build, waiting
        // for its answer" apart from "old build, watch its window instead".
        try { ack = new EventWaitHandle(false, EventResetMode.AutoReset, ackName); }
        catch (Exception) { }
        if (onOpenSettings is not null)
            openSettings = new EventWaitHandle(false, EventResetMode.AutoReset, settingsName);
        var handles = openSettings is null
            ? new WaitHandle[] { activate }
            : new WaitHandle[] { activate, openSettings };
        listener = new Thread(() =>
        {
            while (!disposed)
            {
                try
                {
                    // WaitAny, not WaitOne: the two requests are different verbs
                    // and one must not swallow the other.
                    var index = WaitHandle.WaitAny(handles, 500);
                    if (disposed || index == WaitHandle.WaitTimeout) continue;
                    if (index == 0) onActivate();
                    else onOpenSettings?.Invoke();
                }
                catch (ObjectDisposedException) { return; }
            }
        })
        {
            IsBackground = true,
            Name = "DropLink single-instance"
        };
        listener.Start();
    }

    /// <summary>
    /// Called on the UI thread once a window is really on screen. This is the
    /// only proof a second launch accepts that the running instance is usable.
    /// </summary>
    public void ConfirmAlive()
    {
        try
        {
            using var ack = EventWaitHandle.OpenExisting(AckEventName);
            ack.Set();
        }
        catch (Exception) { /* no waiter, or we are shutting down */ }
    }

    private static void Ask(string eventName, string settingsName, bool settings)
    {
        try
        {
            if (settings)
            {
                using var request = EventWaitHandle.OpenExisting(settingsName);
                request.Set();
            }
            using var existing = EventWaitHandle.OpenExisting(eventName);
            existing.Set();
        }
        catch (Exception) { /* the first instance may be exiting */ }
    }

    /// <param name="ownerPid">0 when the owner could not be identified.</param>
    private static bool WaitForAnswer(string ackName, int ownerPid)
    {
        EventWaitHandle? ack = null;
        try { ack = EventWaitHandle.OpenExisting(ackName); }
        catch (Exception) { /* an owner from before this handshake existed */ }

        if (ack is not null)
        {
            using (ack) return ack.WaitOne(AckTimeout);
        }

        // No handshake to wait on, so watch the only other answer there is: the
        // window the owner was just asked to show. This also keeps the upgrade
        // path honest - a healthy older build pops its window and is left alone
        // instead of being killed to make room for a newer one.
        if (ownerPid <= 0) return true;
        var deadline = DateTime.UtcNow + WindowWatchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
            try
            {
                using var owner = Process.GetProcessById(ownerPid);
                if (owner.MainWindowHandle != IntPtr.Zero) return true;
            }
            catch (Exception)
            {
                // It exited between the request and now: nothing left to take.
                return true;
            }
        }
        return false;
    }

    private static string OwnerPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DropLink", "instance.pid");

    private void WriteOwnerPid()
    {
        try
        {
            var path = OwnerPidPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Environment.ProcessId.ToString());
        }
        catch (Exception error) { AppLog.Write("cannot record instance pid: " + error.Message); }
    }

    private static int ReadOwnerPid()
    {
        try
        {
            var text = File.ReadAllText(OwnerPidPath).Trim();
            return int.TryParse(text, out var pid) && pid > 0 ? pid : 0;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>
    /// Ends the instance that holds the lock without answering. The lock is a
    /// named mutex, so it cannot be stolen - the only way out is for the process
    /// holding it to stop existing.
    /// </summary>
    private bool TryTakeOver(List<Process> others, out int stubbornPid)
    {
        stubbornPid = 0;
        foreach (var candidate in others)
        {
            try
            {
                stubbornPid = candidate.Id;
                AppLog.Write("ending unresponsive instance pid " + candidate.Id);
                candidate.Kill();
                candidate.WaitForExit(5000);
            }
            catch (Exception error)
            {
                AppLog.Write("cannot end instance " + candidate.Id + ": " + error.Message);
                continue;
            }
            // The kernel releases the mutex as the process dies; give it a
            // moment, then claim it. Claiming is what actually matters, not the
            // kill: if another launch won the race, we step aside normally.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    if (mutex.WaitOne(200)) { ownsMutex = true; return true; }
                }
                catch (AbandonedMutexException) { ownsMutex = true; return true; }
            }
            return false;
        }
        return false;
    }

    /// <summary>
    /// Other DropLink processes that could be the one holding the lock: same
    /// login session, and either this installation or the pid that recorded
    /// itself as the owner. Another copy on the same machine is left alone.
    /// </summary>
    private static List<Process> OurProcesses()
    {
        var result = new List<Process>();
        var self = Environment.ProcessId;
        int session;
        try { session = Process.GetCurrentProcess().SessionId; } catch (Exception) { session = -1; }
        var root = AppPaths.PortableRoot();
        var recorded = ReadOwnerPid();

        Process[] all;
        try { all = Process.GetProcessesByName("DropLink"); }
        catch (Exception) { return result; }

        foreach (var process in all)
        {
            if (process.Id == self) { process.Dispose(); continue; }
            try
            {
                // Only ever the same session: the mutex is session-scoped, so a
                // DropLink in another login session cannot be the one holding it.
                if (session >= 0 && process.SessionId != session) { process.Dispose(); continue; }
            }
            catch (Exception) { process.Dispose(); continue; }

            string? path = null;
            try { path = process.MainModule?.FileName; } catch (Exception) { }
            var ours = process.Id == recorded ||
                       (path is not null && root is not null &&
                        path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            if (ours) result.Add(process);
            else process.Dispose();
        }
        return result;
    }

    private static void DisposeAll(List<Process> processes)
    {
        foreach (var process in processes)
        {
            try { process.Dispose(); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { activate?.Set(); } catch (Exception) { }
        if (listener is not null) listener.Join(500);
        activate?.Dispose();
        openSettings?.Dispose();
        ack?.Dispose();
        if (ownsMutex)
        {
            try { mutex.ReleaseMutex(); } catch (Exception) { }
            try
            {
                // Only clear the pid file if it is still ours: a takeover may
                // have handed it to a newer process already.
                if (ReadOwnerPid() == Environment.ProcessId) File.Delete(OwnerPidPath);
            }
            catch (Exception) { }
        }
        mutex.Dispose();
    }
}
