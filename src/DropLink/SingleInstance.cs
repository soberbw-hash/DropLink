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

    private readonly Mutex mutex;
    private readonly bool ownsMutex;
    private readonly EventWaitHandle? activate;
    private readonly EventWaitHandle? openSettings;
    private readonly Thread? listener;
    private volatile bool disposed;

    public bool IsFirst { get; }

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
        mutex = new Mutex(initiallyOwned: true, mutexName, out var created);
        ownsMutex = created;
        IsFirst = created;
        if (!created)
        {
            try
            {
                if (requestOpenSettings)
                {
                    using var settings = EventWaitHandle.OpenExisting(settingsName);
                    settings.Set();
                }
                using var existing = EventWaitHandle.OpenExisting(eventName);
                existing.Set();
            }
            catch (Exception) { /* the first instance may be exiting */ }
            return;
        }

        activate = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { activate?.Set(); } catch (Exception) { }
        if (listener is not null) listener.Join(500);
        activate?.Dispose();
        openSettings?.Dispose();
        if (ownsMutex)
        {
            try { mutex.ReleaseMutex(); } catch (Exception) { }
        }
        mutex.Dispose();
    }
}
