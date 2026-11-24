using System.Diagnostics;

namespace DropLink;

// Verifies the kill-on-close job object really is the lifetime guarantee the
// host relies on. Windows-only; skipped elsewhere.
internal static class ProcessJobChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: job objects are Windows-only");
            return;
        }

        var job = KillOnCloseJob.TryCreate();
        if (job is null || !job.IsValid)
        {
            // Not a failure: a sandbox may forbid job objects. The host degrades
            // to explicit cleanup in that case, which is already covered.
            Console.WriteLine("SKIP: this environment refused to create a job object");
            return;
        }

        using (job)
        {
            using var probe = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (probe is null) throw new Exception("could not start the probe process");
            if (!job.TryAssign(probe)) throw new Exception("could not assign the probe process to the job");

            Thread.Sleep(400);
            if (probe.HasExited) throw new Exception("probe exited before the job was closed");

            job.Dispose();
            if (!probe.WaitForExit(5000))
                throw new Exception("closing the job did not terminate the assigned child");
        }

        Console.WriteLine("PASS: closing the job terminates its assigned children");
    }
}
