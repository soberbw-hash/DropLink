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

    /// <summary>
    /// The layout a person actually downloads: app\DropLink.exe sitting beside
    /// backend\ and data\. Only the prepared-next tree was ever covered, which
    /// is how "double-clicking the exe says the files are incomplete" survived
    /// for as long as it did.
    /// </summary>
    internal static void RunPortable(string scratchRoot)
    {
        var app = Path.Combine(scratchRoot, "app");
        var backend = Path.Combine(scratchRoot, "backend");
        var kit = Path.Combine(scratchRoot, "_backup", "droplink-driver");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(backend);
        Directory.CreateDirectory(Path.Combine(backend, "python"));
        Directory.CreateDirectory(Path.Combine(backend, "airdrop"));
        Directory.CreateDirectory(kit);
        foreach (var name in new[] { "airbridge_radio.exe", "radio_pipe_bridge.exe", "awdl_netd.exe", "awdld.exe" })
            File.WriteAllText(Path.Combine(backend, name), "placeholder");
        File.WriteAllText(Path.Combine(backend, "python", "python.exe"), "placeholder");
        File.WriteAllText(Path.Combine(backend, "airdrop", "engine.py"), "placeholder");
        File.WriteAllText(Path.Combine(kit, "Switch-WinUSB.ps1"), "placeholder");
        File.WriteAllText(Path.Combine(kit, "Restore-Driver.ps1"), "placeholder");

        var keys = new[]
        {
            "DROPLINK_RADIO_EXE", "DROPLINK_BRIDGE_EXE", "DROPLINK_NETD_EXE",
            "DROPLINK_AWDLD_EXE", "DROPLINK_PYTHON", "DROPLINK_ENGINE_SCRIPT",
            "DROPLINK_OUTPUT_DIR", "DROPLINK_KEYS_DIR", "DROPLINK_LOG_DIR",
            "DROPLINK_HARDWARE_FILE", "DROPLINK_DEVICE_NAME", "DROPLINK_SETUP_CMD",
            "DROPLINK_ROLLBACK_CMD", "DROPLINK_AUTHORIZED", "DROPLINK_RECOVERY_VERIFIED"
        };
        var saved = new System.Collections.Generic.List<(string, string?)>();
        foreach (var k in keys) saved.Add((k, Environment.GetEnvironmentVariable(k)));
        try
        {
            foreach (var k in keys) Environment.SetEnvironmentVariable(k, null);
            AppPaths.EnsureEnvironment(app);

            // Everything the readiness probe asks about must be present and
            // must point into backend\, not into app\ or the package root.
            foreach (var k in new[]
                     {
                         "DROPLINK_RADIO_EXE", "DROPLINK_BRIDGE_EXE", "DROPLINK_NETD_EXE",
                         "DROPLINK_AWDLD_EXE", "DROPLINK_PYTHON", "DROPLINK_ENGINE_SCRIPT"
                     })
            {
                var value = Environment.GetEnvironmentVariable(k);
                Require(!string.IsNullOrWhiteSpace(value), k + " filled from the portable layout");
                Require(File.Exists(value!), k + " points to a real file");
                Require(value!.StartsWith(backend, StringComparison.OrdinalIgnoreCase),
                    k + " resolved inside backend\\ (got " + value + ")");
            }

            // Received files and logs belong in data\, never beside the exe.
            foreach (var k in new[] { "DROPLINK_OUTPUT_DIR", "DROPLINK_KEYS_DIR", "DROPLINK_LOG_DIR" })
            {
                var value = Environment.GetEnvironmentVariable(k);
                Require(!string.IsNullOrWhiteSpace(value), k + " filled");
                Require(value!.StartsWith(Path.Combine(scratchRoot, "data"), StringComparison.OrdinalIgnoreCase),
                    k + " resolved inside data\\ (got " + value + ")");
            }

            Require(Environment.GetEnvironmentVariable("DROPLINK_SETUP_CMD") is { Length: > 0 } setup
                    && setup.EndsWith("Switch-WinUSB.ps1", StringComparison.OrdinalIgnoreCase),
                "driver kit resolves to the .ps1, not the .cmd wrapper");

            Console.WriteLine("PASS: portable layout bootstraps every backend path from the exe location");
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
