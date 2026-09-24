using System.Text.Json;
using Susu.Contracts;
using Susu.Domain;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// S02: a package can only use credentials bound to its own identity/declared origin - a changed
/// package identity or origin never inherits an existing account. Uses the same Susu.Domain.
/// CredentialAuthorizer F02 already built and unit-tested; this exercises it wired into Broker's real
/// secret-resolution path (Broker.ResolveSecret), not the pure Domain function in isolation.
/// </summary>
public class AccountAuthorizationTests
{
    private sealed class FakeSecretStore : Susu.Abstractions.ISecretStore
    {
        private readonly Dictionary<(string, string), string> values = [];
        public void Set(string account, string name, string value) => values[(account, name)] = value;
        public bool Has(string a, string n) => values.ContainsKey((a, n));
        public IReadOnlyList<string> Names(string a) => [.. values.Keys.Where(k => k.Item1 == a).Select(k => k.Item2)];
        public void Write(string a, string n, ReadOnlySpan<char> v) => values[(a, n)] = v.ToString();
        public bool Delete(string a, string n) => values.Remove((a, n));
        public bool TryRead(string a, string n, out string v) => values.TryGetValue((a, n), out v!);
    }

    private static IpcEnvelope ApiCallEnvelope(string grant, string pluginId, int callId, string op, string argsJson)
        => new(ProtocolVersions.Ipc, IpcMessageType.ApiCall, "r1", "job", PluginId: pluginId, Grant: grant,
            Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, callId, op, JsonDocument.Parse(argsJson).RootElement), ContractsJson.Default.ApiCallPayload));

    private const string PackageId = "com.example.deepl";
    private const string Signer = "unsigned:com.example.deepl";
    private const string InstanceId = "instance-1";

    private static (AccountAuthorization Accounts, FakeSecretStore Secrets) Setup(string grantedOrigin)
    {
        var secrets = new FakeSecretStore();
        secrets.Set("account-1", "apiKey", "real-secret-value-xyz");
        var account = new AccountSettings("account-1", "My DeepL", ["apiKey"],
            [new CredentialGrant(PackageId, Signer, "apiKey", grantedOrigin, "header:Authorization")]);
        var instance = new InstanceSettings(InstanceId, PackageId, 1, new Dictionary<string, string>(), new Dictionary<string, string> { ["apiKey"] = "account-1" });
        var accounts = new AccountAuthorization(() => ([account], [instance]));
        return (accounts, secrets);
    }

    private static string RequestArgs(string url) =>
        "{\"method\":\"GET\",\"url\":\"" + url + "\",\"headers\":{\"Authorization\":\"\"}," +
        "\"credentials\":[{\"target\":{\"area\":\"header\",\"name\":\"Authorization\"},\"parts\":[{\"literal\":\"Bearer \"},{\"secret\":\"apiKey\"}]}]}";

    [Fact]
    public async Task A_call_from_the_bound_identity_and_granted_origin_is_allowed()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        var (accounts, secrets) = Setup(grantedOrigin: server.Origin);
        using var broker = new Broker(secretStore: secrets, accounts: accounts);
        broker.ApproveLocalOrigin(server.Origin);
        var grant = broker.Issue("r1", PackageId, 1, [server.Origin], secrets: ["apiKey"], instanceId: InstanceId, signer: Signer);

        var result = await broker.HandleAsync(ApiCallEnvelope(grant.Grant, PackageId, 1, "http", RequestArgs(server.Origin + "/x")));

        Assert.True(result.Ok, result.Value.ToString());
    }

    [Fact]
    public async Task A_different_origin_than_the_one_granted_is_denied_even_though_the_secret_name_matches()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        var (accounts, secrets) = Setup(grantedOrigin: "https://other.example:443"); // granted for a DIFFERENT origin
        using var broker = new Broker(secretStore: secrets, accounts: accounts);
        broker.ApproveLocalOrigin(server.Origin);
        var grant = broker.Issue("r1", PackageId, 1, [server.Origin], secrets: ["apiKey"], instanceId: InstanceId, signer: Signer);

        var result = await broker.HandleAsync(ApiCallEnvelope(grant.Grant, PackageId, 1, "http", RequestArgs(server.Origin + "/x")));

        Assert.False(result.Ok);
        Assert.Equal("auth", result.Value.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_different_package_identity_claiming_the_same_secret_name_is_denied()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        var (accounts, secrets) = Setup(grantedOrigin: server.Origin);
        using var broker = new Broker(secretStore: secrets, accounts: accounts);
        broker.ApproveLocalOrigin(server.Origin);
        // A third-party package declaring the same local secret name and even the same instance binding,
        // but a different package id/signer than the grant - must not inherit the account (S02).
        const string impostorPackage = "com.example.impostor";
        var grant = broker.Issue("r1", impostorPackage, 1, [server.Origin], secrets: ["apiKey"], instanceId: InstanceId, signer: "unsigned:com.example.impostor");

        var result = await broker.HandleAsync(ApiCallEnvelope(grant.Grant, impostorPackage, 1, "http", RequestArgs(server.Origin + "/x")));

        Assert.False(result.Ok);
        Assert.Equal("auth", result.Value.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Without_an_AccountAuthorization_wired_the_pre_F07_plugin_id_namespacing_still_works()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        var secrets = new FakeSecretStore();
        secrets.Set(PackageId, "apiKey", "real-secret-value-xyz"); // namespaced by plugin id, the fallback path
        using var broker = new Broker(secretStore: secrets); // no AccountAuthorization supplied
        broker.ApproveLocalOrigin(server.Origin);
        var grant = broker.Issue("r1", PackageId, 1, [server.Origin], secrets: ["apiKey"]);

        var result = await broker.HandleAsync(ApiCallEnvelope(grant.Grant, PackageId, 1, "http", RequestArgs(server.Origin + "/x")));

        Assert.True(result.Ok, result.Value.ToString());
    }
}
