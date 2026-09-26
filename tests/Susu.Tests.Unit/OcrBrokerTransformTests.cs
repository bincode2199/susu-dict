using System.Text.Json;
using Susu.Contracts;
using Susu.Plugins;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F11.2 B05/B06/B08 on the plugin-facing $http path (Broker.HandleAsync → NetworkBroker) for the JSON/Base64 request transform
/// the OCR packages use: corrupt, missing, non-null or overlapping pointers, a pointer shared with a credential target, a wrong
/// encoding or shape, handles outside the grant, and the 48 MiB cap on the final body. Every refusal happens before sending,
/// and every lease the broker took for the call is released again.
/// </summary>
public class OcrBrokerTransformTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6];

    private sealed class Rig : IDisposable
    {
        public readonly FileLeases Leases = new(TestTemp.NewDir("susu-b05-leases"));
        public readonly LoopbackHttpServer Server;
        public readonly Broker Broker;
        public int Hits;
        public LoopbackHttpRequest? Last;

        public Rig()
        {
            Server = new LoopbackHttpServer(req => { Interlocked.Increment(ref Hits); Last = req; return LoopbackHttpResponse.Json(200, """{"ok":true}"""); });
            Broker = new Broker(leases: Leases);
            Broker.ApproveLocalOrigin(Server.Origin);
        }

        public string Lease(byte[]? bytes = null)
        {
            var lease = Leases.Create("ocr", "png");
            File.WriteAllBytes(Leases.PathOf(lease), bytes ?? Png);
            return lease.Id;
        }

        public async Task<(bool Ok, string Value)> HttpAsync(string argsJson, params string[] granted)
        {
            var grant = Broker.Issue("r1", "p1", 1, [Server.Origin], handles: granted);
            var envelope = new IpcEnvelope(ProtocolVersions.Ipc, IpcMessageType.ApiCall, "r1", "j1", PluginId: "p1", Grant: grant.Grant,
                Payload: JsonSerializer.SerializeToElement(new ApiCallPayload(1, 1, "http", JsonDocument.Parse(argsJson.Replace("{origin}", Server.Origin)).RootElement), ContractsJson.Default.ApiCallPayload));
            var result = await Broker.HandleAsync(envelope);
            Broker.Revoke(grant.Grant);
            return (result.Ok, result.Value.GetRawText());
        }

        public void Dispose() { Server.Dispose(); Leases.Dispose(); }
    }

    private static string Request(string value, string bodyFiles, string extra = "")
        => $$"""{"method":"POST","url":"{origin}/ocr","headers":{"Content-Type":"application/json"},"body":{"kind":"json","value":{{value}}},"bodyFiles":{{bodyFiles}}{{extra}}}""";

    [Fact] // the well-formed request passes (baseline for the refusals below)
    public async Task A_reserved_null_pointer_is_filled()
    {
        using var rig = new Rig();
        string id = rig.Lease();
        var (ok, value) = await rig.HttpAsync(Request("""{"ImageBase64":null,"LanguageType":"auto"}""", $$"""[{"pointer":"/ImageBase64","file":"{{id}}","encoding":"base64"}]"""), id);
        Assert.True(ok, value);
        Assert.Equal(Convert.ToBase64String(Png), JsonDocument.Parse(rig.Last!.Body).RootElement.GetProperty("ImageBase64").GetString());
        Assert.DoesNotContain(Convert.ToBase64String(Png), value); // the plugin-visible result never echoes the encoded input
        Assert.Equal(1, rig.Leases.ActiveCount);
    }

    [Theory] // B05: every malformed transform is refused before sending, and the call's lease references are released
    [InlineData("""{"LanguageType":"auto"}""", """[{"pointer":"/ImageBase64","file":"{id}"}]""", "")] // target missing
    [InlineData("""{"ImageBase64":"already-set"}""", """[{"pointer":"/ImageBase64","file":"{id}"}]""", "")] // target not null
    [InlineData("""{"ImageBase64":null}""", """[{"pointer":"/ImageBase64","file":"{id}"},{"pointer":"/ImageBase64","file":"{id}"}]""", "")] // duplicate
    [InlineData("""{"A":[null]}""", """[{"pointer":"/A","file":"{id}"},{"pointer":"/A/0","file":"{id}"}]""", "")] // overlap
    [InlineData("""{"ImageBase64":null}""", """[{"pointer":"ImageBase64","file":"{id}"}]""", "")] // not a JSON pointer
    [InlineData("""{"ImageBase64":null}""", """[{"pointer":"/ImageBase64","file":"{id}","encoding":"hex"}]""", "")] // wrong encoding
    [InlineData("""{"ImageBase64":null}""", """{"pointer":"/ImageBase64","file":"{id}"}""", "")] // not an array
    [InlineData("""{"ImageBase64":null}""", """[{"pointer":"/ImageBase64"}]""", "")] // no handle
    [InlineData("""{"ImageBase64":null}""", """[{"pointer":5,"file":"{id}"}]""", "")] // mistyped pointer
    [InlineData("""{"ImageBase64":null}""", """["/ImageBase64"]""", "")] // entry not an object
    [InlineData("""{"ImageBase64":null}""", """[{"pointer":"/ImageBase64","file":"{id}"}]""",
        ""","credentials":[{"target":{"area":"json","pointer":"/ImageBase64"},"parts":[{"literal":"k"}]}]""")] // same place as a credential
    public async Task Malformed_transforms_are_refused_before_sending(string value, string bodyFiles, string extra)
    {
        using var rig = new Rig();
        string id = rig.Lease();
        var (ok, detail) = await rig.HttpAsync(Request(value, bodyFiles.Replace("{id}", id), extra), id);
        Assert.False(ok);
        Assert.Contains("bad_response", detail);
        Assert.Equal(0, rig.Hits);
        Assert.Equal(1, rig.Leases.ActiveCount); // only the owner's reference remains
        Assert.Equal(Png, File.ReadAllBytes(rig.Leases.PathOf(rig.Leases.AddReference(id)!))); // the input itself is untouched
    }

    [Fact] // bodyFiles only apply to a JSON body
    public async Task BodyFiles_on_a_text_body_are_refused()
    {
        using var rig = new Rig();
        string id = rig.Lease();
        var (ok, _) = await rig.HttpAsync($$"""{"method":"POST","url":"{origin}/ocr","body":{"kind":"text","value":"x"},"bodyFiles":[{"pointer":"/a","file":"{{id}}"}]}""", id);
        Assert.False(ok);
        Assert.Equal(0, rig.Hits);
    }

    [Fact] // B06: a live lease outside the grant is refused in a JSON field and in a multipart part alike
    public async Task Handles_outside_the_grant_are_refused_in_every_body_form()
    {
        using var rig = new Rig();
        string granted = rig.Lease(), foreign = rig.Lease([7, 7, 7]);
        var (json, jsonDetail) = await rig.HttpAsync(Request("""{"ImageBase64":null}""", $$"""[{"pointer":"/ImageBase64","file":"{{foreign}}"}]"""), granted);
        var (multipart, _) = await rig.HttpAsync($$$"""{"method":"POST","url":"{origin}/api","body":{"kind":"multipart","fields":[{"name":"file","filename":"a.png","file":"{{{foreign}}}"}]}}""", granted);
        var (raw, _) = await rig.HttpAsync($$$"""{"method":"POST","url":"{origin}/api","body":{"kind":"file","file":"{{{foreign}}}"}}""", granted);
        Assert.False(json);
        Assert.Contains("not granted", jsonDetail);
        Assert.False(multipart);
        Assert.False(raw);
        Assert.Equal(0, rig.Hits);
        Assert.Equal(2, rig.Leases.ActiveCount);
    }

    [Fact] // B08: a JSON body carrying Base64 files is capped at 48 MiB as sent, even when each file is under the 32 MiB input cap
    public async Task The_final_base64_json_body_is_capped()
    {
        using var rig = new Rig();
        string a = rig.Lease(new byte[20 * 1024 * 1024]), b = rig.Lease(new byte[20 * 1024 * 1024]);
        var (ok, detail) = await rig.HttpAsync(Request("""{"A":null,"B":null}""", $$"""[{"pointer":"/A","file":"{{a}}"},{"pointer":"/B","file":"{{b}}"}]"""), a, b);
        Assert.False(ok);
        Assert.Contains("exceeds", detail);
        Assert.Equal(0, rig.Hits);
        Assert.Equal(2, rig.Leases.ActiveCount);
    }
}
