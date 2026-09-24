using System.Text.Json;
using Susu.Contracts;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F05.4: capability contracts (PLAN 4.4/4.7) run through the real AppContainer sandbox + QuickJS
/// engine + IPC channel + the real Susu.Net.NetworkBroker, against local test servers standing in for
/// real vendors - this environment has no vendor accounts. The fixture "vendor" plugin
/// (fixtures/plugins/vendor) exercises each capability the way a real adapter plugin would: a JSON/
/// multipart request built via ctx.$http, mapped from the vendor's own field names into the PLAN
/// contract shape (Susu.Contracts.PluginApi). Susu.Plugins.CapabilityClient is the minimal reusable
/// adapter later modules (F09-F12/F15) can call directly instead of hand-rolling Invoke/await/parse.
/// </summary>
public class ContractProbeTests
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
        string staged = Path.Combine(Path.GetTempPath(), "susu-contract-it-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Batch translate: BatchItem[] request/result (PLAN 4.4), a vendor JSON array round trip
    /// with no file handles.</summary>
    [Fact]
    public async Task TranslateBatch_round_trips_through_the_real_broker()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200,
            """{"Items":[{"Id":"1","Text":"你好"},{"Id":"2","Text":"世界"}]}"""));
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = $$"""{"url":"{{server.Origin}}/translate","items":[{"id":"1","text":"hello"},{"id":"2","text":"world"}]}""";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "translateBatch", requestJson, "job-batch",
                [server.Origin], ContractsJson.Default.TranslateBatchResult, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Equal(2, outcome.Result!.Items.Length);
            Assert.Equal("你好", outcome.Result.Items[0].Text);
            Assert.Equal("1", outcome.Result.Items[0].Id);
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>Options (manifest schema dynamic loading, PLAN 4.7): items/nextCursor, no file handles.</summary>
    [Fact]
    public async Task Options_round_trips_through_the_real_broker()
    {
        string? staged = StageHost();
        if (staged is null) return;
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200,
            """{"Items":[{"Value":"en","Label":"English"},{"Value":"zh","Label":"Chinese"}],"NextCursor":null}"""));
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = $$"""{"url":"{{server.Origin}}/options","field":"targetLang"}""";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "optionsFetch", requestJson, "job-options",
                [server.Origin], ContractsJson.Default.OptionsResult, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Equal(2, outcome.Result!.Items.Length);
            Assert.Equal("English", outcome.Result.Items[0].Label);
        }
        finally { session.Shutdown(2000); }
    }

    /// <summary>Vocab upsert (PLAN 4.7/F15): operationId/action/word/lang in, status/remoteId out.</summary>
    [Theory]
    [InlineData("applied", "remote-1")]
    [InlineData("found", null)]
    public async Task VocabUpsert_round_trips_through_the_real_broker(string status, string? remoteId)
    {
        string? staged = StageHost();
        if (staged is null) return;
        string remoteIdJson = remoteId is null ? "null" : $"\"{remoteId}\"";
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, $$"""{"Status":"{{status}}","RemoteId":{{remoteIdJson}}}"""));
        using var session = HostSession.Start(new HostSession.Options(Path.Combine(staged, "susu.exe"), staged, "quickjs", KeepProfile: false));
        try
        {
            session.Broker.ApproveLocalOrigin(server.Origin);
            Assert.True(session.Load("vendor", "plugins/vendor").Ok);
            string requestJson = $$"""{"url":"{{server.Origin}}/vocab","operationId":"op-1","action":"upsert","word":"hello","lang":"en"}""";
            var outcome = await CapabilityClient.InvokeAsync(session, "vendor", "vocabUpsert", requestJson, "job-vocab",
                [server.Origin], ContractsJson.Default.VocabResult, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.ErrorDetail);
            Assert.Equal(status, outcome.Result!.Status);
            Assert.Equal(remoteId, outcome.Result.RemoteId);
        }
        finally { session.Shutdown(2000); }
    }
}
