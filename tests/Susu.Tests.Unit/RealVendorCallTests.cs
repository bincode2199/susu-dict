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
