using System.Runtime.Versioning;
using DropLink;

internal static class SingleInstanceChecks
{
    [SupportedOSPlatform("windows")]
    public static void Run()
    {
        var scope = Guid.NewGuid().ToString("N");
        using var activated = new ManualResetEventSlim(false);
        using var first = new SingleInstance(() => activated.Set(), scope);
        if (!first.IsFirst) throw new Exception("first instance was not first");
        using var second = new SingleInstance(() => { }, scope);
        if (second.IsFirst) throw new Exception("second instance claimed first");
        if (!activated.Wait(TimeSpan.FromSeconds(2)))
            throw new Exception("second instance did not activate the existing receiver");
        Console.WriteLine("PASS: single instance activates the existing receiver");
    }
}
