using System;
using System.IO;
using DropLink;

internal static class AppPathsChecks
{
    internal static void Run(string realExeDir)
    {
        var keys = new[]
        {
            "DROPLINK_RADIO_EXE", "DROPLINK_BRIDGE_EXE", "DROPLINK_NETD_EXE",
            "DROPLINK_AWDLD_EXE", "DROPLINK_SETUP_CMD", "DROPLINK_ROLLBACK_CMD",
            "DROPLINK_AUTHORIZED", "DROPLINK_RECOVERY_VERIFIED"
        };
        var saved = new (string, string?)[] { };
        var list = new System.Collections.Generic.List<(string, string?)>();
        foreach (var k in keys) list.Add((k, Environment.GetEnvironmentVariable(k)));
        saved = list.ToArray();
        try
        {
            foreach (var k in keys) Environment.SetEnvironmentVariable(k, null);
            AppPaths.EnsureEnvironment(realExeDir);

            Require(Environment.GetEnvironmentVariable("DROPLINK_AUTHORIZED") == "1", "self-bootstrap sets authorization flag");
            Require(Environment.GetEnvironmentVariable("DROPLINK_RECOVERY_VERIFIED") == "1", "self-bootstrap sets recovery flag");

            foreach (var k in new[] { "DROPLINK_RADIO_EXE", "DROPLINK_BRIDGE_EXE", "DROPLINK_NETD_EXE", "DROPLINK_AWDLD_EXE" })
            {
                var v = Environment.GetEnvironmentVariable(k);
                Require(!string.IsNullOrWhiteSpace(v), k + " filled by self-bootstrap");
                Require(File.Exists(v!), k + " points to a real file");
                Require(v!.Contains("prepared-next"), k + " resolved from work dir");
            }
            foreach (var k in new[] { "DROPLINK_SETUP_CMD", "DROPLINK_ROLLBACK_CMD" })
            {
                var v = Environment.GetEnvironmentVariable(k);
                Require(!string.IsNullOrWhiteSpace(v) && File.Exists(v!), k + " filled and exists");
            }

            // External override must win over self-bootstrap.
            Environment.SetEnvironmentVariable("DROPLINK_RADIO_EXE", "C:\\custom\\radio.exe");
            AppPaths.EnsureEnvironment(realExeDir);
            Require(Environment.GetEnvironmentVariable("DROPLINK_RADIO_EXE") == "C:\\custom\\radio.exe", "external env overrides self-bootstrap");

            Console.WriteLine("PASS: self-contained path bootstrap resolves all backends and honors overrides");
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }

    private static void Require(bool ok, string what)
    {
        if (!ok) throw new Exception("FAILED: " + what);
    }
}
