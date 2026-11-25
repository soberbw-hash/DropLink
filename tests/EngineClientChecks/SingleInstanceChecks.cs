using System.Runtime.Versioning;
using DropLink;

internal static class SingleInstanceChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    [SupportedOSPlatform("windows")]
    public static void Run()
    {
        var scope = Guid.NewGuid().ToString("N");
        using var activated = new ManualResetEventSlim(false);

        // A named mutex is owned per thread, so a second instance created on
        // this thread would be the same owner and would "win" the lock. A real
        // second launch is a separate process, so the second instance has to be
        // built on its own thread for this to mean anything.
        SingleInstance? first = null;
        // The activation callback answers the handshake, the way the real app
        // does once it has actually put a window on screen.
        first = new SingleInstance(() => { activated.Set(); first?.ConfirmAlive(); }, null, scope);
        Require(first.IsFirst, "first instance was not first");
        try
        {
            bool? secondIsFirst = null;
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    using var second = new SingleInstance(() => { }, null, scope);
                    secondIsFirst = second.IsFirst;
                }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true, Name = "second instance" };
            worker.Start();
            Require(worker.Join(TimeSpan.FromSeconds(30)), "second instance did not finish");
            if (failure is not null) throw failure;
            Require(secondIsFirst == false, "second instance claimed first");
            Require(activated.Wait(TimeSpan.FromSeconds(5)),
                "second instance did not activate the existing receiver");
            Console.WriteLine("PASS: single instance activates the existing receiver");
        }
        finally
        {
            first.Dispose();
        }
    }
}
