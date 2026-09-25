using System.Text.Json;
using Susu.Contracts;
using Susu.Plugins;

namespace Susu.PluginCli;

/// <summary>
/// `susu-plugin check|test` (F04.4: the minimal real runtime channel; F16.3 adds `init` and the full
/// author workflow). Both commands validate a plugin package directory: `check` is static (manifest
/// schema, safe file list, structural signature reading); `test` additionally loads the package into
/// the real AppContainer sandbox and QuickJS engine and invokes one declared capability - never a
/// faked/Node runtime, so a package that only "works" outside the sandbox is caught here.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();
        string command = args[0];
        string? dir = args.Length > 1 && !args[1].StartsWith("--") ? Path.GetFullPath(args[1]) : null;
        dir ??= Directory.GetCurrentDirectory();
        return command switch
        {
            "check" => Check(dir),
            "test" => Test(dir, Option(args, "--host"), Option(args, "--capability"), Option(args, "--request") ?? "{}"),
            "-h" or "--help" or "help" => Usage(),
            _ => Usage($"unknown command '{command}'"),
        };
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Usage(string? error = null)
    {
        if (error is not null) Console.Error.WriteLine($"susu-plugin: {error}");
        Console.Error.WriteLine("usage: susu-plugin check <dir>");
        Console.Error.WriteLine("       susu-plugin test <dir> --host <susu.exe> [--capability <name>] [--request <json>]");
        return 2;
    }

    /// <summary>Static validation only: manifest schema, safe file list, structural signature. No sandbox.</summary>
    private static int Check(string dir)
    {
        var (manifest, issues) = ReadManifest(dir);
        foreach (var issue in issues) Console.Error.WriteLine(issue);
        foreach (var issue in SafePackage.Validate(dir)) Console.Error.WriteLine(issue);

        string signaturePath = Path.Combine(dir, "manifest.sig");
        if (File.Exists(signaturePath))
        {
            var (signature, sigIssues) = SignatureFile.Parse(File.ReadAllText(signaturePath));
            foreach (var issue in sigIssues) Console.Error.WriteLine(issue);
            if (signature is not null) Console.WriteLine($"signature: keyId={signature.KeyId} algorithm={signature.Algorithm} (structure only - trust verification is F16/F18)");
        }
        else Console.WriteLine("signature: none (manifest.sig not present - unsigned install is allowed for a first-party test run, PROTOCOL 10)");

        bool ok = manifest is not null && issues.Count == 0 && SafePackage.Validate(dir).Count == 0;
        if (ok) Console.WriteLine($"check: ok - {manifest!.Id} v{manifest.ApiVersion}, capabilities: {string.Join(", ", manifest.Capabilities)}");
        else Console.Error.WriteLine("check: failed");
        return ok ? 0 : 1;
    }

    /// <summary>Loads the package into the real sandbox and invokes one capability through it.</summary>
    private static int Test(string dir, string? hostExe, string? capability, string requestJson)
    {
        var (manifest, issues) = ReadManifest(dir);
        if (manifest is null)
        {
            foreach (var issue in issues) Console.Error.WriteLine(issue);
            Console.Error.WriteLine("test: manifest is invalid; run 'susu-plugin check' first");
            return 1;
        }
        var packageIssues = SafePackage.Validate(dir);
        if (packageIssues.Count > 0)
        {
            foreach (var issue in packageIssues) Console.Error.WriteLine(issue);
            return 1;
        }
        hostExe ??= FindSiblingHost();
        if (hostExe is null || !File.Exists(hostExe))
        {
            Console.Error.WriteLine("test: --host <susu.exe> is required (no susu.exe next to susu-plugin.exe)");
            return 2;
        }
        capability ??= manifest.Capabilities.FirstOrDefault();
        if (capability is null)
        {
            Console.Error.WriteLine("test: manifest declares no capabilities");
            return 1;
        }

        // Manifest hosts are bare host names; Broker grants exact origins. Same https default the host
        // runtime grants a package (Susu.Domain.Origin), so the test run matches production.
        var origins = new List<string>();
        foreach (string manifestHost in manifest.Hosts)
        {
            if (!Susu.Domain.Origin.TryFromManifestHost(manifestHost, out string origin))
            {
                Console.Error.WriteLine($"test: manifest host '{manifestHost}' is not a valid host or origin");
                return 1;
            }
            origins.Add(origin);
        }

        // The AppContainer profile grants read access to exactly one directory: stage the host
        // executable/DLLs plus this package under it (same shape as HostSession's other callers).
        string staged = Path.Combine(Path.GetTempPath(), "susu-plugin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        try
        {
            string hostDir = Path.GetDirectoryName(hostExe)!;
            foreach (string file in Directory.EnumerateFiles(hostDir))
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, Path.Combine(staged, Path.GetFileName(file)), overwrite: true);
            string pluginDir = Path.Combine(staged, "plugins", manifest.Id);
            CopyDirectory(dir, pluginDir);

            using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, Path.GetFileName(hostExe)), staged, "quickjs"));
            try
            {
                var loaded = session.Load(manifest.Id, Path.Combine("plugins", manifest.Id).Replace('\\', '/'), timeoutMs: 10000);
                if (!loaded.Ok)
                {
                    Console.Error.WriteLine($"test: load failed: {loaded.Error}");
                    return 1;
                }
                Console.WriteLine($"test: loaded {manifest.Id} in {loaded.Milliseconds:F1} ms, engine {loaded.EngineBytes} bytes");

                var (_, _, task) = session.Invoke(manifest.Id, capability, requestJson, jobId: "susu-plugin-test", origins: origins);
                var envelope = task.Wait(TimeSpan.FromSeconds(30)) ? task.Result : throw new TimeoutException($"capability '{capability}' did not respond within 30 s");
                var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
                if (envelope.Type == IpcMessageType.Completed && completed.Ok)
                {
                    Console.WriteLine($"test: {capability} -> ok: {completed.Result!.Value.GetRawText()}");
                    return 0;
                }
                Console.Error.WriteLine($"test: {capability} -> failed: {completed.Error?.Kind} {completed.Error?.Detail}");
                return 1;
            }
            finally { session.Shutdown(2000); }
        }
        finally { try { Directory.Delete(staged, recursive: true); } catch (IOException) { } }
    }

    private static (PackageManifest? Manifest, IReadOnlyList<ManifestIssue> Issues) ReadManifest(string dir)
    {
        string path = Path.Combine(dir, "manifest.yaml");
        if (!File.Exists(path)) return (null, [new ManifestIssue("$", "missing", "manifest.yaml not found")]);
        return PackageManifest.Parse(File.ReadAllText(path));
    }

    private static string? FindSiblingHost()
    {
        string candidate = Path.Combine(AppContext.BaseDirectory, "susu.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
