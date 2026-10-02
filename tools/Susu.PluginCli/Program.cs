using System.Text.Json;
using System.Text.RegularExpressions;
using Susu.Contracts;
using Susu.Plugins;
using Susu.Plugins.Install;

namespace Susu.PluginCli;

/// <summary>
/// `susu-plugin` developer CLI (F04.4 `check`/`test`, F16.3 `init`, case-file `test`, `pack`, `keygen`).
/// Exit codes: 0 success, 1 the package or a case failed, 2 usage or an unreadable input (bad arguments, malformed cases file, no host).
/// `test` loads the package into the real AppContainer sandbox and QuickJS engine through the same host process, IPC and broker the product
/// uses - never a faked/Node runtime, so a package that only "works" outside the sandbox is caught here.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();
        string command = args[0];
        string? positional = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        string dir = positional is null ? Directory.GetCurrentDirectory() : Path.GetFullPath(positional);
        try
        {
            return command switch
            {
                "init" => Init(positional, Option(args, "--capability"), Option(args, "--id"), Option(args, "--name")),
                "check" => Check(dir),
                "test" => Option(args, "--request") is not null || Option(args, "--capability") is not null
                    ? AdHocTest(dir, Option(args, "--host"), Option(args, "--capability"), Option(args, "--request") ?? "{}")
                    : CaseTest(dir, Option(args, "--host"), Option(args, "--cases"), Option(args, "--filter")),
                "pack" => Pack.Run(dir, Option(args, "--out"), Option(args, "--key")),
                "keygen" => positional is null ? Usage("keygen needs a file name for the private seed") : Pack.Keygen(positional),
                "-h" or "--help" or "help" => Usage(),
                _ => Usage($"unknown command '{command}'"),
            };
        }
        catch (CaseFileException e)
        {
            Console.Error.WriteLine($"susu-plugin: test file: {e.Message}");
            return 2;
        }
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Usage(string? error = null)
    {
        if (error is not null) Console.Error.WriteLine($"susu-plugin: {error}");
        Console.Error.WriteLine("usage: susu-plugin init <dir> --capability <" + string.Join("|", Templates.Capabilities) + "> [--id <reverse.domain.id>] [--name <name>]");
        Console.Error.WriteLine("       susu-plugin check [<dir>]");
        Console.Error.WriteLine("       susu-plugin test [<dir>] [--host <susu.exe>] [--cases <file>] [--filter <text>]");
        Console.Error.WriteLine("       susu-plugin test <dir> --host <susu.exe> --capability <name> --request <json>   (one ad-hoc call, real vendor hosts)");
        Console.Error.WriteLine("       susu-plugin pack [<dir>] [--out <file.susuext>] [--key <seed file>]");
        Console.Error.WriteLine("       susu-plugin keygen <seed file>");
        Console.Error.WriteLine("exit codes: 0 ok, 1 package or case failed, 2 usage or unreadable input");
        return 2;
    }

    /// <summary>Writes a working starter package for one capability into a new or empty folder.</summary>
    private static int Init(string? target, string? capability, string? id, string? name)
    {
        if (target is null) return Usage("init needs a target folder");
        if (capability is null || !Templates.Capabilities.Contains(capability)) return Usage($"init needs --capability, one of: {string.Join(", ", Templates.Capabilities)}");
        string dir = Path.GetFullPath(target);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) { Console.Error.WriteLine($"init: {dir} is not empty; nothing was written"); return 2; }
        string folder = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        if (id is null)
        {
            string slug = new([.. folder.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit)]);
            id = "com.example." + (slug.Length == 0 || !char.IsAsciiLetter(slug[0]) ? "plugin" + slug : slug);
        }
        if (!Susu.Domain.PackageId.IsValid(id)) { Console.Error.WriteLine($"init: '{id}' is not a reverse-domain package id (for example com.yourname.{capability}); pass --id"); return 2; }
        name ??= char.ToUpperInvariant(folder[0]) + folder[1..];
        name = name.Replace("\"", "").Replace("\n", " ");
        Directory.CreateDirectory(dir);
        foreach (var (file, text) in Templates.Files(capability, id, name))
            File.WriteAllText(Path.Combine(dir, file), text, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"init: wrote a {capability} plugin ({id}) to {dir}");
        Console.WriteLine("init: next: susu-plugin check; susu-plugin test (the cases file is " + CaseFile.DefaultName + ")");
        return 0;
    }

    /// <summary>Static validation only: manifest schema, safe file list, entry file, cases file syntax, signature. No sandbox.</summary>
    private static int Check(string dir)
    {
        var (manifest, issues) = ReadManifest(dir);
        var problems = new List<ManifestIssue>(issues);
        problems.AddRange(SafePackage.Validate(dir));

        if (manifest is not null)
        {
            string entryPath = Path.Combine(dir, manifest.Entry);
            if (!File.Exists(entryPath)) problems.Add(new ManifestIssue("entry", "missing", $"entry file '{manifest.Entry}' is not in the package"));
            else
            {
                string source = File.ReadAllText(entryPath);
                if (!Regex.IsMatch(source, @"export\s+default")) problems.Add(new ManifestIssue(manifest.Entry, "no-default-export", "the entry must `export default { ... }` an object of capability methods"));
                else
                    foreach (string capability in manifest.Capabilities)
                        if (!Regex.IsMatch(source, @"(^|[\s,{])(async\s+)?" + Regex.Escape(capability) + @"\s*(\(|:)", RegexOptions.Multiline))
                            Console.WriteLine($"warning: {manifest.Entry}: no method named '{capability}' found for the declared capability (calls to it will fail)");
            }
        }

        string casesPath = Path.Combine(dir, CaseFile.DefaultName);
        if (File.Exists(casesPath))
        {
            try
            {
                var cases = CaseFile.Load(casesPath);
                if (manifest is not null)
                    foreach (var c in cases.Where(c => c.Capability is not null && !manifest.Capabilities.Contains(c.Capability, StringComparer.OrdinalIgnoreCase) && c.Capability is not ("voices" or "options")))
                        problems.Add(new ManifestIssue(CaseFile.DefaultName, "capability", $"case '{c.Name}' uses capability '{c.Capability}' that the manifest does not declare"));
                Console.WriteLine($"tests: {CaseFile.DefaultName} parses ({cases.Count} case(s))");
            }
            catch (CaseFileException e) { problems.Add(new ManifestIssue(CaseFile.DefaultName, "invalid", e.Message)); }
        }
        else Console.WriteLine($"tests: no {CaseFile.DefaultName} (add one for 'susu-plugin test')");

        // The signature is verified for real (Ed25519 over the manifest and the file list); only a self-certifying third-party key can be
        // checked here, host keys belong to the host's own keyring.
        var (identity, signatureIssues) = PackageTrust.Verify(dir, HostKeyring.Empty);
        problems.AddRange(signatureIssues);
        if (identity is { Kind: SignerKind.ThirdParty }) Console.WriteLine($"signature: valid, key {identity.Signer} (a third-party key: the user is shown it at install, it is not an endorsement)");
        else if (identity is { Kind: SignerKind.Unsigned }) Console.WriteLine("signature: none (unsigned packages can be installed after the user confirms; sign with 'susu-plugin pack --key')");

        foreach (var issue in problems) Console.Error.WriteLine(issue);
        bool ok = manifest is not null && problems.Count == 0;
        if (ok) Console.WriteLine($"check: ok - {manifest!.Id} v{manifest.ApiVersion}, capabilities: {string.Join(", ", manifest.Capabilities)}");
        else Console.Error.WriteLine("check: failed");
        return ok ? 0 : 1;
    }

    private static string? ResolveHost(string? hostExe)
    {
        hostExe ??= Path.Combine(AppContext.BaseDirectory, "susu.exe");
        return File.Exists(hostExe) ? Path.GetFullPath(hostExe) : null;
    }

    /// <summary>Replays the cases of a cases file through the real sandbox against a loopback vendor.</summary>
    private static int CaseTest(string dir, string? hostExe, string? casesFile, string? filter)
    {
        var (manifest, issues) = ReadManifest(dir);
        var packageIssues = manifest is null ? [] : SafePackage.Validate(dir);
        if (manifest is null || packageIssues.Count > 0)
        {
            foreach (var issue in issues.Concat(packageIssues)) Console.Error.WriteLine(issue);
            Console.Error.WriteLine("test: the package is invalid; run 'susu-plugin check' first");
            return 1;
        }
        casesFile = casesFile is null ? Path.Combine(dir, CaseFile.DefaultName) : Path.GetFullPath(casesFile);
        if (!File.Exists(casesFile))
        {
            Console.Error.WriteLine($"test: {casesFile} not found (write cases, or run one ad-hoc call with --capability and --request)");
            return 2;
        }
        var cases = CaseFile.Load(casesFile);
        string? host = ResolveHost(hostExe);
        if (host is null)
        {
            Console.Error.WriteLine("test: --host <susu.exe> is required (no susu.exe next to susu-plugin.exe)");
            return 2;
        }
        return TestRunner.Run(manifest, dir, host, cases, filter, Console.Out, Console.Error);
    }

    /// <summary>The original single-call mode (F04.4): one capability, one request, the package's own declared hosts.</summary>
    private static int AdHocTest(string dir, string? hostExe, string? capability, string requestJson)
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
        string? host = ResolveHost(hostExe);
        if (host is null)
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

        string staged = TestRunner.Stage(host, dir, manifest.Id, out string pluginPath);
        try
        {
            using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, Path.GetFileName(host)), staged, "quickjs"));
            try
            {
                var loaded = session.Load(manifest.Id, pluginPath, timeoutMs: 10000);
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
}
