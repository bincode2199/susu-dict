using System.Text.Json;
using Susu.Contracts;
using Susu.Plugins;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F05: one real vendor call through the production path (real AppContainer sandbox, real
/// Susu.Net.NetworkBroker, real DNS/TLS to the actual internet) - MyMemory's free, keyless endpoint,
/// the only vendor this environment can reach without an account. Evidence that a genuinely-replayed
/// (loopback test server) call and a real network call go through the identical broker code path, not
/// two different implementations. Sanitized metadata only (status/text-length/timestamp) is written to
/// the scratchpad; no request/response body or header content that could carry anything sensitive.
/// </summary>
public class RealVendorCallTests
{
    private static string? FindHostBuildOutput()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish");
            if (File.Exists(Path.Combine(candidate, "susu.exe"))) return candidate;
        }
        return null;
    }

    private static string? StageHost()
    {
        string? output = FindHostBuildOutput();
        if (output is null) return null;
        string staged = Path.Combine(Path.GetTempPath(), "susu-real-vendor-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string plugins = Path.Combine(staged, "plugins");
        Directory.CreateDirectory(plugins);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins"), "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(plugins, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return staged;
    }

    /// <summary>Also stages the real, shipped MyMemory built-in package (src/Susu.Host/plugins/mymemory,
    /// published alongside susu.exe) next to the fixtures - F06.1's own real-vendor evidence, calling
    /// "translate" through the actual production adapter rather than the echo fixture's generic httpGet.</summary>
    private static string? StageHostWithMyMemoryPackage()
    {
        string? output = FindHostBuildOutput();
        if (output is null) return null;
        string sourcePlugin = Path.Combine(output, "plugins", "mymemory");
        if (!File.Exists(Path.Combine(sourcePlugin, "main.js"))) return null;
        string? staged = StageHost();
        if (staged is null) return null;
        string targetPlugin = Path.Combine(staged, "plugins", "mymemory");
        Directory.CreateDirectory(targetPlugin);
        foreach (string file in Directory.EnumerateFiles(sourcePlugin))
            File.Copy(file, Path.Combine(targetPlugin, Path.GetFileName(file)));
        return staged;
    }

    [Fact] // F06.1: the real production adapter (not the echo fixture's generic httpGet) against the real vendor
    public async Task MyMemory_package_translate_answers_through_the_real_sandbox_and_broker()
    {
        string? staged = StageHostWithMyMemoryPackage();
        if (staged is null) return;
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            Assert.True(session.Load("app.susu.mymemory", "plugins/mymemory").Ok);
            string requestJson = JsonSerializer.Serialize(new TranslateRequest("hello", "en", "zh-Hans"), ContractsJson.Default.TranslateRequest);
            var (_, _, task) = session.Invoke("app.susu.mymemory", "translate", requestJson, jobId: "job-mymemory-real",
                origins: ["https://api.mymemory.translated.net"]);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(IpcMessageType.Completed, envelope.Type);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.True(completed.Ok, completed.Error?.Detail);
            var result = completed.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
            Assert.False(string.IsNullOrWhiteSpace(result.Text));

            string scratchpad = Environment.GetEnvironmentVariable("CLAUDE_SCRATCHPAD_DIR")
                ?? Path.Combine(Path.GetTempPath(), "susu-f06-evidence");
            Directory.CreateDirectory(scratchpad);
            File.WriteAllText(Path.Combine(scratchpad, "f06-mymemory-package-real-call.json"), JsonSerializer.Serialize(new
            {
                timestampUtc = DateTime.UtcNow.ToString("O"),
                origin = "https://api.mymemory.translated.net",
                package = "app.susu.mymemory",
                translatedTextLength = result.Text.Length,
                note = "sanitized: no request/response body or headers persisted, only shape/length",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { session.Shutdown(2000); }
    }

    [Fact]
    public async Task MyMemory_free_endpoint_answers_through_the_real_sandbox_and_broker()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            Assert.True(session.Load("echo", "plugins/echo").Ok);
            string url = "https://api.mymemory.translated.net/get?q=hello&langpair=en|zh-CN";
            var (_, _, task) = session.Invoke("echo", "httpGet", "{\"url\":\"" + url + "\"}", jobId: "job-mymemory",
                origins: ["https://api.mymemory.translated.net"]);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(IpcMessageType.Completed, envelope.Type);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.True(completed.Ok, completed.Error?.Detail);
            var result = completed.Result!.Value;
            int status = result.GetProperty("status").GetInt32();
            Assert.Equal(200, status);
            string? translated = result.GetProperty("body").GetProperty("responseData").GetProperty("translatedText").GetString();
            Assert.False(string.IsNullOrWhiteSpace(translated));

            string scratchpad = Environment.GetEnvironmentVariable("CLAUDE_SCRATCHPAD_DIR")
                ?? Path.Combine(Path.GetTempPath(), "susu-f05-evidence");
            Directory.CreateDirectory(scratchpad);
            string evidencePath = Path.Combine(scratchpad, "f05-mymemory-real-call.json");
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
            {
                timestampUtc = DateTime.UtcNow.ToString("O"),
                origin = "https://api.mymemory.translated.net",
                status,
                translatedTextLength = translated!.Length,
                note = "sanitized: no request/response body or headers persisted, only shape/status/length",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { session.Shutdown(2000); }
    }
}
