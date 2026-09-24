using System.Text.Json;
using Susu.Contracts;
using Susu.Net;
using Susu.Plugins;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// S10 (TEST-PLAN): a response that echoes back a known literal form of an injected credential - raw,
/// URL-encoded or standard Base64 - is intercepted before the plugin sees it. Per PLAN 4.5.4.5 this is
/// defense in depth only: any other transformation of the value is explicitly out of scope and not
/// something a test can assert away (CredentialLeakScannerTests.Other_transformations_are_not_caught
/// documents that boundary rather than pretending it does not exist).
/// </summary>
public class CredentialLeakScannerTests
{
    private static BrokerHttpResponse Response(string? textBody = null, System.Text.Json.Nodes.JsonNode? jsonBody = null, IReadOnlyDictionary<string, string>? headers = null)
        => new(200, headers ?? new Dictionary<string, string>(), jsonBody, textBody, null, null, [], false, null);

    [Fact]
    public void Raw_form_in_the_JSON_body_is_caught()
    {
        var response = Response(jsonBody: System.Text.Json.Nodes.JsonNode.Parse("""{"echo":"received: my-secret-value-1"}"""));
        Assert.True(CredentialLeakScanner.ContainsKnownForm(response, ["my-secret-value-1"]));
    }

    [Fact]
    public void Url_encoded_form_is_caught()
    {
        string secret = "a value/with+special=chars";
        string encoded = Uri.EscapeDataString(secret);
        var response = Response(textBody: $"redirect?token={encoded}");
        Assert.True(CredentialLeakScanner.ContainsKnownForm(response, [secret]));
    }

    [Fact]
    public void Base64_form_is_caught()
    {
        string secret = "another-secret-value";
        string base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret));
        var response = Response(textBody: $"debug: key={base64}");
        Assert.True(CredentialLeakScanner.ContainsKnownForm(response, [secret]));
    }

    [Fact]
    public void Header_echo_is_caught()
    {
        var response = Response(headers: new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Echo"] = "Bearer my-secret-value-1" });
        Assert.True(CredentialLeakScanner.ContainsKnownForm(response, ["my-secret-value-1"]));
    }

    [Fact]
    public void Unrelated_content_is_not_flagged()
    {
        var response = Response(jsonBody: System.Text.Json.Nodes.JsonNode.Parse("""{"status":"ok","id":"12345"}"""));
        Assert.False(CredentialLeakScanner.ContainsKnownForm(response, ["my-secret-value-1"]));
    }

    [Fact]
    public void Short_values_are_not_scanned_to_avoid_false_positives()
    {
        var response = Response(textBody: "ok"); // the literal secret "ok" appearing in ordinary text
        Assert.False(CredentialLeakScanner.ContainsKnownForm(response, ["ok"]));
    }

    /// <summary>Documents PLAN 4.5.4.5's explicit boundary: a server-side transformation the scanner does
    /// not know (e.g. reversing, hashing, wrapping in a different encoding) is not detected - S10 says
    /// this must be recorded, not silently promised away.</summary>
    [Fact]
    public void Other_transformations_are_not_caught()
    {
        string secret = "my-secret-value-1";
        string reversed = new(secret.Reverse().ToArray());
        var response = Response(textBody: $"debug: {reversed}");
        Assert.False(CredentialLeakScanner.ContainsKnownForm(response, [secret]));
    }
}

/// <summary>End-to-end S10 through Broker.HandleAsync (the real production op handler) against a real
/// loopback server that echoes the credential back - not just the pure scanner function.</summary>
public class CredentialLeakInterceptionTests
{
    private sealed class FakeSecretStore(string accountId, string name, string value) : Susu.Abstractions.ISecretStore
    {
        public bool Has(string a, string n) => a == accountId && n == name;
        public IReadOnlyList<string> Names(string a) => a == accountId ? [name] : [];
        public void Write(string a, string n, ReadOnlySpan<char> v) => throw new NotSupportedException();
        public bool Delete(string a, string n) => throw new NotSupportedException();
        public bool TryRead(string a, string n, out string v) { v = a == accountId && n == name ? value : ""; return a == accountId && n == name; }
    }

    private static IpcEnvelope ApiCallEnvelope(string grant, string requestId, string pluginId, int callId, string op, string argsJson)
        => new(ProtocolVersions.Ipc, IpcMessageType.ApiCall, requestId, "job", PluginId: pluginId, Grant: grant,
            Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, callId, op, JsonDocument.Parse(argsJson).RootElement), ContractsJson.Default.ApiCallPayload));

    [Fact]
    public async Task A_server_that_echoes_the_Authorization_value_back_is_intercepted()
    {
        const string apiKey = "super-secret-key-0123456789";
        LoopbackHttpRequest? seen = null;
        using var server = new LoopbackHttpServer(req =>
        {
            seen = req;
            string received = req.Headers.TryGetValue("Authorization", out var value) ? value : "";
            return LoopbackHttpResponse.Json(200, "{\"echo\":\"" + received.Replace("\"", "") + "\"}");
        });
        using var broker = new Broker(secretStore: new FakeSecretStore("plugin-x", "apiKey", apiKey));
        broker.ApproveLocalOrigin(server.Origin);
        var grant = broker.Issue("r1", "plugin-x", 1, [server.Origin], secrets: ["apiKey"]);

        string args = "{\"method\":\"GET\",\"url\":\"" + server.Origin + "/x\",\"headers\":{\"Authorization\":\"\"}," +
            "\"credentials\":[{\"target\":{\"area\":\"header\",\"name\":\"Authorization\"},\"parts\":[{\"literal\":\"Bearer \"},{\"secret\":\"apiKey\"}]}]}";
        var result = await broker.HandleAsync(ApiCallEnvelope(grant.Grant, "r1", "plugin-x", 1, "http", args));

        Assert.False(result.Ok);
        Assert.Equal("bad_response", result.Value.GetProperty("kind").GetString());
        Assert.NotNull(seen); // the request really was sent (and really did carry the real key) before the response was intercepted
    }

    [Fact]
    public async Task A_server_that_does_not_echo_anything_is_not_flagged()
    {
        const string apiKey = "super-secret-key-0123456789";
        using var server = new LoopbackHttpServer(_ => LoopbackHttpResponse.Json(200, """{"ok":true}"""));
        using var broker = new Broker(secretStore: new FakeSecretStore("plugin-x", "apiKey", apiKey));
        broker.ApproveLocalOrigin(server.Origin);
        var grant = broker.Issue("r1", "plugin-x", 1, [server.Origin], secrets: ["apiKey"]);

        string args = "{\"method\":\"GET\",\"url\":\"" + server.Origin + "/x\",\"headers\":{\"Authorization\":\"\"}," +
            "\"credentials\":[{\"target\":{\"area\":\"header\",\"name\":\"Authorization\"},\"parts\":[{\"literal\":\"Bearer \"},{\"secret\":\"apiKey\"}]}]}";
        var result = await broker.HandleAsync(ApiCallEnvelope(grant.Grant, "r1", "plugin-x", 1, "http", args));

        Assert.True(result.Ok, result.Value.ToString());
    }
}
