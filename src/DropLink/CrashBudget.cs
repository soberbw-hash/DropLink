namespace DropLink;

// Decides whether a crashing engine deserves another cheap restart or whether
// the whole radio chain has to be rebuilt. A single exit is usually a one-off;
// several inside a short window means the chain underneath is broken, and
// restarting the engine forever would just hide that.
//
// The clock is injected so the rule can be tested without waiting ten minutes.
internal sealed class CrashBudget
{
    private readonly Queue<long> stamps = new();
    private readonly long windowMs;
    private readonly int budget;
    private readonly Func<long> clock;

    internal CrashBudget(int budget, long windowMs, Func<long>? clock = null)
    {
        if (budget < 1) throw new ArgumentOutOfRangeException(nameof(budget));
        if (windowMs <= 0) throw new ArgumentOutOfRangeException(nameof(windowMs));
        this.budget = budget;
        this.windowMs = windowMs;
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    internal void Reset()
    {
        lock (stamps) stamps.Clear();
    }

    // Records one crash and reports whether the budget is now exhausted.
    internal bool Record()
    {
        var now = clock();
        lock (stamps)
        {
            stamps.Enqueue(now);
            while (stamps.Count > 0 && now - stamps.Peek() >= windowMs) stamps.Dequeue();
            return stamps.Count >= budget;
        }
    }
}
