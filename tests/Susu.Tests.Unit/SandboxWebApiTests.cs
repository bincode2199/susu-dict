using System.Text.Json;
using System.Text.Json.Nodes;
using Susu.Runtime;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F16.4: the Web APIs PLAN 4.3 promises that the QuickJS child runtime lacked: <c>crypto.getRandomValues</c>,
/// <c>crypto.randomUUID</c>, <c>crypto.subtle.digest</c> and <c>URLSearchParams</c>. These tests drive the real
/// QuickJS bridge in-process (no AppContainer); the same code runs in the sandbox test in
/// <see cref="PluginAuthorCliTests"/>-style CLI cases (tests/plugin-cases/webapis).
/// </summary>
public class SandboxWebApiTests
{
    private const string Plugin = """
        const hex = (buf) => Array.from(new Uint8Array(buf), (x) => x.toString(16).padStart(2, '0')).join('');
        const ascii = (s) => Uint8Array.from(s, (c) => c.charCodeAt(0));
        const err = (e) => e.name + ':' + (e instanceof Error);
        const settle = async (p) => { try { return await p; } catch (e) { return err(e); } };
        const attempt = (f) => { try { return f(); } catch (e) { return err(e); } };
        const q = (init) => new URLSearchParams(init);
        export default {
          async digest(req) {
            const long = 'abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq';
            const padded = new Uint8Array(10); padded.set(ascii('abc'), 3);
            return {
              sha1: hex(await crypto.subtle.digest('SHA-1', ascii('abc'))),
              sha1Empty: hex(await crypto.subtle.digest('SHA-1', new Uint8Array(0))),
              sha256: hex(await crypto.subtle.digest('SHA-256', ascii('abc'))),
              sha256Empty: hex(await crypto.subtle.digest('SHA-256', new ArrayBuffer(0))),
              sha256Long: hex(await crypto.subtle.digest('SHA-256', ascii(long))),
              sha384: hex(await crypto.subtle.digest('SHA-384', ascii('abc'))),
              sha512: hex(await crypto.subtle.digest('SHA-512', ascii('abc'))),
              objectLower: hex(await crypto.subtle.digest({ name: 'sha-256' }, ascii('abc'))),
              viewWithOffset: hex(await crypto.subtle.digest('SHA-256', padded.subarray(3, 6))),
              buffer: hex(await crypto.subtle.digest('SHA-256', ascii('abc').buffer)),
              isArrayBuffer: (await crypto.subtle.digest('SHA-256', ascii('x'))) instanceof ArrayBuffer,
              md5: await settle(crypto.subtle.digest('MD5', ascii('abc'))),
              badData: await settle(crypto.subtle.digest('SHA-256', 'abc')),
              noArgs: await settle(crypto.subtle.digest()),
            };
          },
          async random() {
            const a = new Uint8Array(32), b = new Uint8Array(32);
            const same = crypto.getRandomValues(a) === a;
            crypto.getRandomValues(b);
            const wide = new Uint32Array(16384);              // exactly 65536 bytes
            crypto.getRandomValues(wide);
            const buf = new Uint8Array(12);                   // only the view's window may change
            crypto.getRandomValues(buf.subarray(4, 8));
            const big = crypto.getRandomValues(new BigUint64Array(4));
            const uuids = new Set(); for (let i = 0; i < 200; i++) uuids.add(crypto.randomUUID());
            return {
              same, nonZero: a.some((x) => x !== 0), differ: Array.from(a).join() !== Array.from(b).join(),
              wideNonZero: wide.some((x) => x !== 0), wideLength: wide.byteLength,
              edgesUntouched: Array.from(buf.subarray(0, 4)).concat(Array.from(buf.subarray(8))).every((x) => x === 0),
              windowTouched: buf.subarray(4, 8).some((x) => x !== 0) || true,
              big: big instanceof BigUint64Array && big.length === 4,
              empty: crypto.getRandomValues(new Uint8Array(0)).length,
              quota: attempt(() => crypto.getRandomValues(new Uint8Array(65537))),
              quotaWide: attempt(() => crypto.getRandomValues(new Uint32Array(16385))),
              float: attempt(() => crypto.getRandomValues(new Float32Array(4))),
              view: attempt(() => crypto.getRandomValues(new DataView(new ArrayBuffer(4)))),
              plain: attempt(() => crypto.getRandomValues([1, 2, 3])),
              uuidFormat: Array.from(uuids).every((u) => /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(u)),
              uuidUnique: uuids.size,
              surface: {
                frozen: Object.isFrozen(crypto) && Object.isFrozen(crypto.subtle),
                subtleKeys: Object.keys(crypto.subtle), cryptoKeys: Object.keys(crypto),
                sign: typeof crypto.subtle.sign, importKey: typeof crypto.subtle.importKey, key: typeof crypto.subtle.generateKey,
                replace: attempt(() => { 'use strict'; globalThis.crypto = 1; return typeof globalThis.crypto; }),
              },
            };
          },
          async params(req) {
            const out = {};
            out.parse = [...q('?a=1&b=2&a=3')];
            out.noQuestion = [...q('a=1')];
            out.empty = [[...q('')], [...q('?')], [...q()], [...q(null)], q('').toString()];
            out.emptyParts = [...q('&&a=1&&&b&=c&=')];
            out.plusSpace = [q('a=b+c').get('a'), q('a=b%20c').get('a'), q('a+b=1').get('a b')];
            out.encodePlus = [new URLSearchParams({ a: 'b c' }).toString(), new URLSearchParams({ a: 'b+c' }).toString(), new URLSearchParams({ 'a b': '1' }).toString()];
            out.malformed = [q('a=%').get('a'), q('a=%zz').get('a'), q('a=%4').get('a'), q('a=100%25').get('a'), q('a=%e4%bd').get('a'), q('a=%ff').get('a')];
            out.unicode = [q('k=%E4%BD%A0%E5%A5%BD').get('k'), new URLSearchParams({ k: '你好 \u{1F600}' }).toString(), q('%E4%BD%A0=1').has('你')];
            out.lone = new URLSearchParams([['a', '\ud800'], ['b\udc00', 'x']]).toString();
            out.reserved = new URLSearchParams({ a: "*-._~!'()/?:@&=+$,;#" }).toString();
            out.noValue = [q('a').get('a'), q('a=').get('a'), q('=b').get(''), q('a=b=c').get('a')];
            const d = q('a=1&b=2&a=3&c=4');
            out.dupes = [d.get('a'), d.getAll('a'), d.getAll('zz'), d.get('zz'), d.has('a'), d.has('zz'), d.has('a', '3'), d.has('a', '9'), d.size];
            d.append('a', '5'); out.append = d.toString();
            d.delete('a', '3'); out.deleteValue = d.toString();
            d.delete('a'); out.deleteAll = d.toString();
            const s = q('a=1&b=2&a=3&c=4');
            s.set('a', 'X'); out.setExisting = s.toString();
            s.set('n', 'new'); out.setNew = s.toString();
            const o = q('z=1&a=2&b=3&a=1&é=4&B=5&%F0%9F%98%80=6');
            o.sort(); out.sort = o.toString();
            out.iter = [[...q('a=1&b=2').keys()], [...q('a=1&b=2').values()], [...q('a=1&b=2').entries()], [...q('a=1&b=2')[Symbol.iterator]()]];
            const seen = []; q('a=1&b=2').forEach(function (v, k, p) { seen.push(k + v + (p instanceof URLSearchParams) + (this.t)); }, { t: 'T' });
            out.forEach = seen;
            const copy = q(q('a=1&a=2')); copy.append('b', '3'); out.copy = copy.toString();
            out.pairs = q([['a', '1'], ['a', '2']]).toString();
            out.record = q({ x: 1, y: true, z: null }).toString();
            out.badPairs = [attempt(() => q([['a']])), attempt(() => q([1])), attempt(() => q([['a', 'b', 'c']]))];
            out.needArgs = [attempt(() => q('').append('a')), attempt(() => q('').get()), attempt(() => q('').set('a')), attempt(() => q('').has())];
            out.live = (() => { const p = q('a=1&b=2'); const r = []; for (const [k] of p) { r.push(k); if (k === 'a') p.append('c', '3'); } return r; })();
            out.numberInit = q(5).toString() + '|' + q('?x=1&y=2').toString();
            out.tag = [Object.prototype.toString.call(q('')), String(q('a=1')), `${q('a=b c')}`];
            out.sizeEmpty = q('').size;
            out.leadingQuestionOnce = [...q('??a=1')];
            return out;
          },
        };
        """;

    private sealed class Callbacks : IRuntimeCallbacks
    {
        public string? Completed;
        public int ApiCall(string pluginId, int apiId, string json) => 1;
        void IRuntimeCallbacks.Completed(string pluginId, string json) => Completed = json;
        public void Log(string pluginId, string json) { }
    }

    private static JsonElement Call(string capability)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "susu_quickjs.dll"))) { Assert.Skip("susu_quickjs.dll not built (tools/build-native.ps1)"); }
        string root = TestTemp.NewDir("susu-webapi");
        File.WriteAllText(Path.Combine(root, "main.js"), Plugin);
        var callbacks = new Callbacks();
        using var budget = new ExecutionBudget();
        using var runtime = QuickJsRuntime.Create("test.webapi", root, 64, budget, callbacks);
        Assert.Null(runtime.Load("main.js", out int status));
        Assert.Equal(0, status);
        Assert.Equal(0, runtime.Invoke(1, capability, "{}", "{}"));
        Assert.NotNull(callbacks.Completed);
        using var doc = JsonDocument.Parse(callbacks.Completed!);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), callbacks.Completed);
        return doc.RootElement.GetProperty("result").Clone();
    }

    private static string S(JsonElement e, string path)
    {
        foreach (string part in path.Split('.')) e = e.GetProperty(part);
        return e.ToString();
    }

    private static string Json(JsonElement e, string path)
    {
        foreach (string part in path.Split('.')) e = e.GetProperty(part);
        return e.GetRawText();
    }

    [Fact]
    public void Digest_matches_the_published_SHA_vectors_and_rejects_bad_input()
    {
        var r = Call("digest");
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", S(r, "sha1"));
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", S(r, "sha1Empty"));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", S(r, "sha256"));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", S(r, "sha256Empty"));
        Assert.Equal("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1", S(r, "sha256Long"));
        Assert.Equal("cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7", S(r, "sha384"));
        Assert.Equal("ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f", S(r, "sha512"));
        Assert.Equal(S(r, "sha256"), S(r, "objectLower")); // algorithm names are case-insensitive, object form accepted
        Assert.Equal(S(r, "sha256"), S(r, "viewWithOffset")); // only the view's window is hashed, not its whole buffer
        Assert.Equal(S(r, "sha256"), S(r, "buffer"));
        Assert.True(r.GetProperty("isArrayBuffer").GetBoolean());
        Assert.Equal("NotSupportedError:true", S(r, "md5"));
        Assert.Equal("TypeError:true", S(r, "badData"));
        Assert.Equal("NotSupportedError:true", S(r, "noArgs"));
    }

    [Fact]
    public void Random_values_respect_type_and_the_65536_byte_quota_and_UUIDs_are_v4()
    {
        var r = Call("random");
        Assert.True(r.GetProperty("same").GetBoolean());
        Assert.True(r.GetProperty("nonZero").GetBoolean());
        Assert.True(r.GetProperty("differ").GetBoolean());
        Assert.True(r.GetProperty("wideNonZero").GetBoolean());
        Assert.Equal("65536", S(r, "wideLength"));
        Assert.True(r.GetProperty("edgesUntouched").GetBoolean());
        Assert.True(r.GetProperty("big").GetBoolean());
        Assert.Equal("0", S(r, "empty"));
        Assert.Equal("QuotaExceededError:true", S(r, "quota"));
        Assert.Equal("QuotaExceededError:true", S(r, "quotaWide"));
        Assert.Equal("TypeMismatchError:true", S(r, "float"));
        Assert.Equal("TypeMismatchError:true", S(r, "view"));
        Assert.Equal("TypeMismatchError:true", S(r, "plain"));
        Assert.True(r.GetProperty("uuidFormat").GetBoolean());
        Assert.Equal("200", S(r, "uuidUnique"));
    }

    [Fact]
    public void Crypto_surface_is_exactly_the_planned_three_functions_and_cannot_be_replaced()
    {
        var r = Call("random");
        Assert.True(r.GetProperty("surface").GetProperty("frozen").GetBoolean());
        Assert.Equal("""["digest"]""", Json(r, "surface.subtleKeys"));
        Assert.Equal("""["getRandomValues","randomUUID","subtle"]""", Json(r, "surface.cryptoKeys"));
        Assert.Equal("undefined", S(r, "surface.sign"));
        Assert.Equal("undefined", S(r, "surface.importKey"));
        Assert.Equal("undefined", S(r, "surface.key"));
        Assert.NotEqual("number", S(r, "surface.replace")); // assignment to the global is refused (TypeError string) or ignored
    }

    [Theory]
    [InlineData("parse", """[["a","1"],["b","2"],["a","3"]]""")]
    [InlineData("noQuestion", """[["a","1"]]""")]
    [InlineData("empty", "[[],[],[],[],\"\"]")]
    [InlineData("emptyParts", """[["a","1"],["b",""],["","c"],["",""]]""")]
    [InlineData("plusSpace", """["b c","b c","1"]""")]
    [InlineData("encodePlus", """["a=b+c","a=b%2Bc","a+b=1"]""")]
    [InlineData("malformed", """["%","%zz","%4","100%","�","�"]""")]
    [InlineData("unicode", """["你好","k=%E4%BD%A0%E5%A5%BD+%F0%9F%98%80",true]""")]
    [InlineData("lone", "\"a=%EF%BF%BD&b%EF%BF%BD=x\"")]
    [InlineData("reserved", "\"a=*-._%7E%21%27%28%29%2F%3F%3A%40%26%3D%2B%24%2C%3B%23\"")]
    [InlineData("noValue", """["","","b","b=c"]""")]
    [InlineData("dupes", """["1",["1","3"],[],null,true,false,true,false,4]""")]
    [InlineData("append", "\"a=1&b=2&a=3&c=4&a=5\"")]
    [InlineData("deleteValue", "\"a=1&b=2&c=4&a=5\"")]
    [InlineData("deleteAll", "\"b=2&c=4\"")]
    [InlineData("setExisting", "\"a=X&b=2&c=4\"")]
    [InlineData("setNew", "\"a=X&b=2&c=4&n=new\"")]
    [InlineData("sort", "\"B=5&a=2&a=1&b=3&z=1&%C3%A9=4&%F0%9F%98%80=6\"")]
    [InlineData("iter", """[["a","b"],["1","2"],[["a","1"],["b","2"]],[["a","1"],["b","2"]]]""")]
    [InlineData("forEach", """["a1trueT","b2trueT"]""")]
    [InlineData("copy", "\"a=1&a=2&b=3\"")]
    [InlineData("pairs", "\"a=1&a=2\"")]
    [InlineData("record", "\"x=1&y=true&z=null\"")]
    [InlineData("badPairs", """["TypeError:true","TypeError:true","TypeError:true"]""")]
    [InlineData("needArgs", """["TypeError:true","TypeError:true","TypeError:true","TypeError:true"]""")]
    [InlineData("live", """["a","b","c"]""")]
    [InlineData("numberInit", "\"5=|x=1&y=2\"")]
    [InlineData("tag", """["[object URLSearchParams]","a=1","a=b+c"]""")]
    [InlineData("sizeEmpty", "0")]
    [InlineData("leadingQuestionOnce", """[["?a","1"]]""")]
    public void URLSearchParams_follows_the_URL_standard(string key, string expectedJson)
    {
        var r = Call("params");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), JsonNode.Parse(Json(r, key))), key + ": " + Json(r, key));
    }
}
