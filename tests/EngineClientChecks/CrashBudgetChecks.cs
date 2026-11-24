namespace DropLink;

// Verifies the rule that separates "restart the engine" from "rebuild the
// radio". The clock is injected, so these checks are deterministic and never
// sleep.
internal static class CrashBudgetChecks
{
    public static void Run()
    {
        // Three crashes inside ten minutes must trip the budget on the third.
        var now = 0L;
        var budget = new CrashBudget(3, 600_000, () => now);
        now = 1_000;
        if (budget.Record()) throw new Exception("first crash must not rebuild the chain");
        now = 5_000;
        if (budget.Record()) throw new Exception("second crash must not rebuild the chain");
        now = 9_000;
        if (!budget.Record()) throw new Exception("third crash in the window must rebuild the chain");

        // A reset (fresh chain start) clears the history.
        budget.Reset();
        now = 20_000;
        if (budget.Record()) throw new Exception("reset must clear the crash history");

        // Crashes spread further apart than the window never accumulate: a
        // process that dies once an hour is not evidence of a broken radio.
        budget.Reset();
        now = 0;
        if (budget.Record()) throw new Exception("isolated crash must not rebuild");
        now = 700_000;
        if (budget.Record()) throw new Exception("crash after the window must not accumulate");
        now = 1_400_000;
        if (budget.Record()) throw new Exception("crash after the window must not accumulate");

        // A crash exactly on the window boundary counts as expired, so the
        // window is a hard ten minutes rather than eleven.
        budget.Reset();
        now = 0;
        budget.Record();
        now = 300_000;
        budget.Record();
        now = 600_000;
        if (budget.Record()) throw new Exception("the oldest crash must expire at the window boundary");

        Console.WriteLine("PASS: crash budget restarts the engine until three crashes land inside ten minutes");
    }
}
