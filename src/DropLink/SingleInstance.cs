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

    private readonly Mutex mutex;
    private readonly bool ownsMutex;
    private readonly EventWaitHandle? activate;
    private readonly Thread? listener;
    private volatile bool disposed;

    public bool IsFirst { get; }

    public SingleInstance(Action onActivate, string? scope = null)
    {
        var suffix = string.IsNullOrWhiteSpace(scope) ? "" : "." + scope;
        var mutexName = MutexName + suffix;
        var eventName = ActivateEventName + suffix;
        mutex = new Mutex(initiallyOwned: true, mutexName, out var created);
        ownsMutex = created;
        IsFirst = created;
        if (!created)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(eventName);
                existing.Set();
            }
            catch (Exception) { /* the first instance may be exiting */ }
            return;
        }

        activate = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        listener = new Thread(() =>
        {
            while (!disposed)
            {
                try
                {
                    if (activate.WaitOne(500) && !disposed) onActivate();
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
        if (ownsMutex)
        {
            try { mutex.ReleaseMutex(); } catch (Exception) { }
        }
        mutex.Dispose();
    }
}
