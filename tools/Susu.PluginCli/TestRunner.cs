using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Plugins;
using Susu.Storage;

namespace Susu.PluginCli;

/// <summary>
/// `susu-plugin test` (cases mode): stages the host binaries plus the package, starts the REAL sandboxed plugin host (same AppContainer,
/// QuickJS engine, IPC channel and Broker/NetworkBroker the product uses) and replays each case against a loopback vendor that answers
/// with the case's canned responses. Nothing is faked on the plugin side: a Node or in-process stand-in is never involved.
/// </summary>
internal static class TestRunner
{
    public static string Stage(string hostExe, string packageDir, string pluginId, out string pluginPath)
    {
        string staged = Path.Combine(Path.GetTempPath(), "susu-plugin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        string hostDir = Path.GetDirectoryName(hostExe)!;
        foreach (string file in Directory.EnumerateFiles(hostDir))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)), overwrite: true);
        pluginPath = Path.Combine("plugins", pluginId).Replace('\\', '/');
        CopyPackage(packageDir, Path.Combine(staged, "plugins", pluginId));
        return staged;
    }

    /// <summary>Copies the package as it would be installed: author-only files (the cases file, a tests folder) stay behind.</summary>
    private static void CopyPackage(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (Pack.IsAuthorOnly(relative)) continue;
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<(string, string), string> values = [];
        public bool Has(string a, string n) => values.ContainsKey((a, n));
        public IReadOnlyList<string> Names(string a) => [.. values.Keys.Where(k => k.Item1 == a).Select(k => k.Item2)];
        public void Write(string a, string n, ReadOnlySpan<char> v) => values[(a, n)] = v.ToString();
        public bool Delete(string a, string n) => values.Remove((a, n));
        public bool TryRead(string a, string n, out string v) => values.TryGetValue((a, n), out v!);
    }

    private sealed record Outcome(bool Ok, JsonNode? Result, string? ErrorKind, string? Detail);

    public static int Run(PackageManifest manifest, string packageDir, string hostExe, List<TestCase> cases, string? filter, TextWriter output, TextWriter error)
    {
        var selected = cases.Where(c => filter is null || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0) { error.WriteLine($"test: no case name contains '{filter}'"); return 2; }
        foreach (var c in selected)
        {
            string capability = c.Capability ?? manifest.Capabilities.FirstOrDefault() ?? "";
            if (!manifest.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase) && c.Capability is not null && !IsAuxiliary(capability))
            {
                error.WriteLine($"test: case '{c.Name}': capability '{capability}' is not declared in the manifest ({string.Join(", ", manifest.Capabilities)})");
                return 2;
            }
        }

        string staged = Stage(hostExe, packageDir, manifest.Id, out string pluginPath);
        string leaseDir = Path.Combine(Path.GetTempPath(), "susu-plugin-test-leases-" + Guid.NewGuid().ToString("N"));
        var leases = new FileLeases(leaseDir);
        var secrets = new MemorySecrets();
        HostSession? session = null;
        int passed = 0, failed = 0;
        try
        {
            output.WriteLine($"test: {manifest.Id} - {selected.Count} case(s) through the real sandbox");
            foreach (var testCase in selected)
            {
                var watch = Stopwatch.StartNew();
                string? failure;
                try
                {
                    if (session is null)
                    {
                        session = HostSession.Start(new HostSession.Options(Path.Combine(staged, Path.GetFileName(hostExe)), staged, "quickjs", MakeBroker: () => new Broker(leases: leases, secretStore: secrets)));
                        var loaded = session.Load(manifest.Id, pluginPath, timeoutMs: 10000);
                        if (!loaded.Ok)
                        {
                            error.WriteLine($"test: load failed: {loaded.Error}");
                            session.Dispose();
                            return 1;
                        }
                    }
                    failure = RunCase(session, manifest, testCase, secrets, leases, out bool restart);
                    if (restart) { session.Dispose(); session = null; }
                }
                catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException)
                {
                    failure = $"harness error: {e.Message}";
                    try { session?.Dispose(); } catch (Exception) { }
                    session = null;
                }
                watch.Stop();
                if (failure is null) { passed++; output.WriteLine($"  PASS  {testCase.Name}  ({watch.ElapsedMilliseconds} ms)"); }
                else { failed++; output.WriteLine($"  FAIL  {testCase.Name}  ({watch.ElapsedMilliseconds} ms)"); error.WriteLine($"        {failure}"); }
            }
        }
        finally
        {
            if (session is not null) { try { session.Shutdown(2000); } catch (Exception) { } session.Dispose(); }
            leases.Dispose();
            try { Directory.Delete(leaseDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(staged, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        output.WriteLine($"test: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // `voices` and `options` are manifest-declared helpers of a capability, callable like one.
    private static bool IsAuxiliary(string capability) => capability is "voices" or "options";

    private static string? RunCase(HostSession session, PackageManifest manifest, TestCase testCase, MemorySecrets secrets, FileLeases leases, out bool restart)
    {
        restart = false;
        string capability = testCase.Capability ?? manifest.Capabilities.First();

        int replyIndex = 0;
        using var vendor = new LoopbackVendor(_ =>
        {
            if (testCase.Vendor.Count == 0) return new VendorResponse(404, Encoding.UTF8.GetBytes("{\"error\":\"no canned vendor response in this case\"}"), "application/json", new Dictionary<string, string>(), 0);
            var reply = testCase.Vendor[Math.Min(Interlocked.Increment(ref replyIndex) - 1, testCase.Vendor.Count - 1)];
            return new VendorResponse(reply.Status, reply.Body, reply.ContentType, reply.Headers, reply.DelayMs);
        });
        session.Broker.ApproveLocalOrigin(vendor.Origin);

        // Input files become real host leases; the plugin sees only the opaque handle, as in production.
        var handleInfo = new Dictionary<string, JsonObject>();
        var handleIds = new List<string>();
        foreach (var (name, input) in testCase.Inputs)
        {
            var lease = leases.Create("plugin-test-" + name, input.Extension);
            File.WriteAllBytes(leases.PathOf(lease), input.Bytes);
            handleIds.Add(lease.Id);
            var info = new JsonObject { ["id"] = lease.Id, ["mime"] = input.Mime, ["bytes"] = input.Bytes.Length };
            if (input.DurationMs is { } d) info["durationMs"] = d;
            if (input.Width is { } w) info["width"] = w;
            if (input.Height is { } h) info["height"] = h;
            handleInfo[name] = info;
        }

        string? substitutionError = null;
        JsonNode request = Substitute(testCase.Request.DeepClone(), vendor.Origin, handleInfo, ref substitutionError)!;
        if (substitutionError is not null) return substitutionError;
        var config = (JsonObject)Substitute(testCase.Config.DeepClone(), vendor.Origin, handleInfo, ref substitutionError)!;
        if (!config.ContainsKey("baseUrl")) config["baseUrl"] = vendor.Origin; // the convention every shipped package follows

        foreach (string stale in secrets.Names(manifest.Id)) secrets.Delete(manifest.Id, stale);
        foreach (var (name, value) in testCase.Secrets) secrets.Write(manifest.Id, name, value);

        var (requestId, callId, task) = session.Invoke(manifest.Id, capability, request.ToJsonString(), jobId: "susu-plugin-test", origins: [vendor.Origin],
            secrets: testCase.Secrets.Keys, configJson: config.ToJsonString(), handles: handleIds);
        Outcome outcome;
        try
        {
            if (!task.Wait(testCase.TimeoutMs))
            {
                try { session.Cancel(manifest.Id, requestId, "susu-plugin-test", callId); } catch (IOException) { }
                restart = true; // a plugin that ignored the cancel must not leak into the next case
                outcome = new Outcome(false, null, "timeout", $"no answer within {testCase.TimeoutMs} ms");
            }
            else outcome = Read(task.Result);
        }
        catch (AggregateException e) when (e.InnerException is IOException)
        {
            restart = true;
            outcome = new Outcome(false, null, "unavailable", "the plugin host process ended while the call was open");
        }

        // vendor requests first: they explain most surprises
        if (testCase.ExpectRequests is { } expectedRequests)
        {
            var seen = vendor.Requests;
            if (seen.Count != expectedRequests.Count)
                return $"the plugin sent {seen.Count} vendor request(s), expected {expectedRequests.Count}" + Describe(seen);
            for (int i = 0; i < seen.Count; i++)
                if (CheckRequest(expectedRequests[i], seen[i], i) is { } mismatch) return mismatch;
        }

        if (testCase.ExpectError is { } expectedKind)
        {
            if (outcome.Ok) return $"expected error '{expectedKind}', but the call succeeded with {CaseFile.Short(outcome.Result)}";
            if (outcome.ErrorKind != expectedKind) return $"expected error '{expectedKind}', got '{outcome.ErrorKind}' ({outcome.Detail})";
            if (testCase.ExpectDetailContains is { } part && outcome.Detail?.Contains(part, StringComparison.Ordinal) != true)
                return $"error detail '{outcome.Detail}' does not contain '{part}'";
            return null;
        }
        if (!outcome.Ok) return $"expected a result, got error '{outcome.ErrorKind}' ({outcome.Detail})" + Describe(vendor.Requests);
        return CaseFile.Match(testCase.ExpectResult, outcome.Result);
    }

    private static string Describe(IReadOnlyList<VendorRequest> seen)
        => seen.Count == 0 ? "" : "; vendor saw " + string.Join(", ", seen.Select(r => $"{r.Method} {Uri.UnescapeDataString(r.Path)}"));

    private static string? CheckRequest(RequestExpect expected, VendorRequest actual, int index)
    {
        string at = $"vendor request #{index + 1} ({actual.Method} {Uri.UnescapeDataString(actual.Path)})";
        if (expected.Method is not null && expected.Method != actual.Method) return $"{at}: expected method {expected.Method}";
        string path = Uri.UnescapeDataString(actual.Path);
        if (expected.Path is not null && expected.Path != path) return $"{at}: expected path {expected.Path}";
        if (expected.PathContains is not null && !path.Contains(expected.PathContains, StringComparison.Ordinal)) return $"{at}: path does not contain '{expected.PathContains}'";
        if (expected.BodyContains is not null && !actual.BodyText.Contains(expected.BodyContains, StringComparison.Ordinal)) return $"{at}: body does not contain '{expected.BodyContains}'";
        if (expected.BodyJson is not null)
        {
            JsonNode? body;
            try { body = JsonNode.Parse(actual.Body); }
            catch (JsonException) { return $"{at}: body is not JSON"; }
            if (CaseFile.Match(expected.BodyJson, body, "body") is { } mismatch) return $"{at}: {mismatch}";
        }
        foreach (var (name, part) in expected.HeaderContains)
        {
            if (!actual.Headers.TryGetValue(name, out string? value)) return $"{at}: header '{name}' missing";
            if (!value.Contains(part, StringComparison.Ordinal)) return $"{at}: header '{name}' does not contain '{part}'";
        }
        return null;
    }

    private static Outcome Read(IpcEnvelope envelope)
    {
        var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
        if (envelope.Type == IpcMessageType.Completed && completed.Ok)
            return new Outcome(true, completed.Result is { } r ? JsonNode.Parse(r.GetRawText()) : null, null, null);
        return new Outcome(false, null, WireName(ErrorKinds.FromPlugin(completed.Error?.Kind)), completed.Error?.Detail);
    }

    public static string WireName(ErrorKind kind) => kind switch
    {
        ErrorKind.Auth => "auth", ErrorKind.Quota => "quota", ErrorKind.RateLimited => "rate_limited", ErrorKind.Network => "network",
        ErrorKind.Timeout => "timeout", ErrorKind.UnsupportedLanguage => "unsupported_language", ErrorKind.BadResponse => "bad_response",
        ErrorKind.Cancelled => "cancelled", ErrorKind.Busy => "busy", _ => "unavailable",
    };

    /// <summary>Replaces the literal "$vendor" inside strings with the loopback origin and <c>{"$file":"name"}</c> with the case input's handle.</summary>
    private static JsonNode? Substitute(JsonNode? node, string origin, Dictionary<string, JsonObject> handles, ref string? problem)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.Count == 1 && o.TryGetPropertyValue("$file", out var fileName))
                {
                    string? name = fileName is JsonValue fv && fv.TryGetValue<string>(out string? s) ? s : null;
                    if (name is null || !handles.TryGetValue(name, out var info)) { problem ??= $"{{\"$file\": {fileName?.ToJsonString()}}} names no entry of \"inputs\""; return o; }
                    return info.DeepClone();
                }
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    var current = o[key];
                    var replaced = Substitute(current, origin, handles, ref problem);
                    if (!ReferenceEquals(current, replaced)) o[key] = replaced;
                }
                return o;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    var current = a[i];
                    var replaced = Substitute(current, origin, handles, ref problem);
                    if (!ReferenceEquals(current, replaced)) a[i] = replaced;
                }
                return a;
            case JsonValue v when v.TryGetValue<string>(out string? text) && text.Contains("$vendor", StringComparison.Ordinal):
                return JsonValue.Create(text.Replace("$vendor", origin, StringComparison.Ordinal));
            default:
                return node;
        }
    }
}
