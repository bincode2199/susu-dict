using System.Text.Json;
using Susu.Contracts;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F06.1: the real, shipped MyMemory package (src/Susu.Host/plugins/mymemory, not a test fixture) through
/// the real AppContainer sandbox + QuickJS engine + IPC + Broker/$http pipeline, against a local
/// LoopbackHttpServer standing in for the vendor - same shape as F05's vendor-contract probes. Needs
/// susu.exe already published (`dotnet publish src/Susu.Host -c Release -r win-x64`, as
/// RealVendorCallTests/PluginHostIntegrationTests also require); skips itself otherwise.
/// </summary>
public class MyMemoryPluginTests
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

    /// <summary>Stages susu.exe/DLLs plus the *real* shipped mymemory package (published alongside
    /// susu.exe, not a fixtures/plugins copy) into the AppContainer's scoped resource directory.</summary>
    private static string? StageHost()
    {
        string? output = FindHostBuildOutput();
        if (output is null) return null;
        string sourcePlugin = Path.Combine(output, "plugins", "mymemory");
        if (!File.Exists(Path.Combine(sourcePlugin, "main.js"))) return null;
        string staged = TestTemp.NewDir("susu-mymemory-it");
        foreach (string file in Directory.EnumerateFiles(output))
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        string targetPlugin = Path.Combine(staged, "plugins", "mymemory");
        Directory.CreateDirectory(targetPlugin);
        foreach (string file in Directory.EnumerateFiles(sourcePlugin))
            File.Copy(file, Path.Combine(targetPlugin, Path.GetFileName(file)));
        return staged;
    }

    private static HostSession.Options Options(string staged) => new(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false);

    [Fact] // P-T01: the real package, real sandbox, langpair maps our canonical zh-Hans to MyMemory's zh-CN
    public async Task Real_package_maps_canonical_languages_and_calls_the_configured_base_url()
    {
        string? staged = StageHost();
        if (staged is null) return;
        string? capturedPath = null;
        using var server = new LoopbackHttpServer(req =>
        {
            capturedPath = Uri.UnescapeDataString(req.Path);
            return LoopbackHttpResponse.Json(200, "{\"responseData\":{\"translatedText\":\"你好\"},\"responseStatus\":200}");
        });
        using var session = HostSession.Start(Options(staged));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("app.susu.mymemory", "plugins/mymemory").Ok);
            string requestJson = JsonSerializer.Serialize(new TranslateRequest("hello", "en", "zh-Hans"), ContractsJson.Default.TranslateRequest);
            string configJson = JsonSerializer.Serialize(new { baseUrl = server.Origin });
            var (_, _, task) = session.Invoke("app.susu.mymemory", "translate", requestJson, jobId: "job-mm-1", origins: [server.Origin], configJson: configJson);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(IpcMessageType.Completed, envelope.Type);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.True(completed.Ok, completed.Error?.Detail);
            var result = completed.Result!.Value.Deserialize(ContractsJson.Default.TranslateResult)!;
            Assert.Equal("你好", result.Text);
            Assert.NotNull(capturedPath);
            Assert.Contains("q=hello", capturedPath);
            Assert.Contains("langpair=en|zh-CN", capturedPath); // our canonical zh-Hans mapped to MyMemory's own zh-CN tag
        }
        finally { session.Shutdown(2000); }
    }

    [Fact] // MyMemory answers HTTP 200 with a quota message in the body once the shared daily allowance is used
    public async Task Quota_message_in_a_200_response_maps_to_the_quota_error_kind()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200,
            "{\"responseData\":{\"translatedText\":\"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY.\"},\"responseStatus\":200}"));
        using var session = HostSession.Start(Options(staged));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("app.susu.mymemory", "plugins/mymemory").Ok);
            string requestJson = JsonSerializer.Serialize(new TranslateRequest("hello", "en", "zh-Hans"), ContractsJson.Default.TranslateRequest);
            string configJson = JsonSerializer.Serialize(new { baseUrl = server.Origin });
            var (_, _, task) = session.Invoke("app.susu.mymemory", "translate", requestJson, jobId: "job-mm-2", origins: [server.Origin], configJson: configJson);
            var envelope = await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var completed = envelope.Payload!.Value.Deserialize(ContractsJson.Default.CompletedPayload)!;
            Assert.False(completed.Ok);
            Assert.Equal(ErrorKind.Quota, ErrorKinds.FromPlugin(completed.Error?.Kind));
        }
        finally { session.Shutdown(2000); }
    }
}
