using System.Diagnostics;
using System.Text;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F16.3: the author workflow of the published `susu-plugin.exe` (init, check, test, pack, keygen) against the published susu.exe: every
/// generated template and every shipped package goes through the REAL sandbox, plus the error cases of the cases file (malformed file, wrong
/// expectation, timeout, a plugin that throws, runs out of memory or never returns). Skips (visibly) when either executable is not
/// published: `dotnet publish tools/Susu.PluginCli -c Release -r win-x64` and `dotnet publish src/Susu.Host -c Release -r win-x64`.
/// </summary>
public class PluginAuthorCliTests
{
    private static string? FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static (string Cli, string Host)? Exes()
    {
        string? cli = FindUp(Path.Combine("tools", "Susu.PluginCli", "bin", "Release", "net10.0", "win-x64", "publish", "susu-plugin.exe"));
        string? host = FindUp(Path.Combine("src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish", "susu.exe"));
        return cli is null || host is null ? null : (cli, host);
    }

    private static (int Exit, string Out, string Err) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000)) { process.Kill(true); throw new TimeoutException($"{exe} {string.Join(' ', args)} did not finish"); }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    [Theory]
    [InlineData("translate")]
    [InlineData("dictionary")]
    [InlineData("detect")]
    [InlineData("ocr")]
    [InlineData("tts")]
    [InlineData("asr")]
    [InlineData("vocab")]
    public void Init_then_check_then_test_passes_for_every_template(string capability)
    {
        if (Exes() is not var (cli, host)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string dir = Path.Combine(TestTemp.NewDir("susu-author"), capability);

        var init = Run(cli, "init", dir, "--capability", capability);
        Assert.True(init.Exit == 0, init.Err);
        Assert.True(File.Exists(Path.Combine(dir, "manifest.yaml")) && File.Exists(Path.Combine(dir, "main.js")) && File.Exists(Path.Combine(dir, "susu-plugin.test.json")));

        var check = Run(cli, "check", dir);
        Assert.True(check.Exit == 0, check.Out + check.Err);
        Assert.DoesNotContain("warning", check.Out);
        Assert.Contains($"capabilities: {capability}", check.Out);

        var test = Run(cli, "test", dir, "--host", host);
        Assert.True(test.Exit == 0, test.Out + test.Err);
        Assert.Contains(" 0 failed", test.Out);
        Assert.DoesNotContain("FAIL", test.Out);
    }

    [Fact]
    public void Init_refuses_a_non_empty_folder_and_a_bad_id_or_capability()
    {
        if (Exes() is not var (cli, _)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string root = TestTemp.NewDir("susu-author");
        File.WriteAllText(Path.Combine(root, "keep.txt"), "x");
        var busy = Run(cli, "init", root, "--capability", "translate");
        Assert.Equal(2, busy.Exit);
        Assert.Equal("x", File.ReadAllText(Path.Combine(root, "keep.txt"))); // nothing overwritten

        Assert.Equal(2, Run(cli, "init", Path.Combine(root, "a"), "--capability", "teleport").Exit);
        var badId = Run(cli, "init", Path.Combine(root, "b"), "--capability", "translate", "--id", "NotAnId");
        Assert.Equal(2, badId.Exit);
        Assert.Contains("--id", badId.Err);
        Assert.False(Directory.Exists(Path.Combine(root, "b")));
    }

    /// <summary>The exit criterion: every shipped package passes `check` and its recorded cases through the real sandbox.</summary>
    [Theory]
    [InlineData("ankiconnect")]
    [InlineData("deepl")]
    [InlineData("eudic")]
    [InlineData("gemini-asr")]
    [InlineData("google-tts")]
    [InlineData("microsoft-tts")]
    [InlineData("mymemory")]
    [InlineData("openai")]
    [InlineData("openai-asr")]
    [InlineData("simple-latex")]
    [InlineData("tencent-ocr")]
    [InlineData("tencent-translate")]
    [InlineData("tencent-tts")]
    [InlineData("youdao")]
    public void Every_shipped_plugin_passes_check_and_its_recorded_cases(string directory)
    {
        if (Exes() is not var (cli, host)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string dir = Path.Combine(Path.GetDirectoryName(host)!, "plugins", directory);
        string? cases = FindUp(Path.Combine("tests", "plugin-cases", directory + ".test.json"));
        Assert.NotNull(cases);

        var check = Run(cli, "check", dir);
        Assert.True(check.Exit == 0, check.Out + check.Err);
        Assert.DoesNotContain("warning", check.Out);

        var test = Run(cli, "test", dir, "--host", host, "--cases", cases);
        Assert.True(test.Exit == 0, test.Out + test.Err);
        Assert.Contains(" 0 failed", test.Out);
    }

    [Fact]
    public void Every_shipped_plugin_has_a_cases_file()
    {
        string? source = FindUp(Path.Combine("src", "Susu.Host", "plugins"));
        if (source is null) { Assert.Skip("source tree not found"); return; }
        string? cases = FindUp(Path.Combine("tests", "plugin-cases"));
        Assert.NotNull(cases);
        foreach (string plugin in Directory.GetDirectories(source))
            Assert.True(File.Exists(Path.Combine(cases, Path.GetFileName(plugin) + ".test.json")), $"{Path.GetFileName(plugin)} has no tests/plugin-cases file");
    }

    // ---- error cases of the cases file ----

    private static string Generated(string capability, string? main = null, string? cases = null)
    {
        var (cli, _) = Exes()!.Value;
        string dir = Path.Combine(TestTemp.NewDir("susu-author"), capability);
        Assert.Equal(0, Run(cli, "init", dir, "--capability", capability).Exit);
        if (main is not null) File.WriteAllText(Path.Combine(dir, "main.js"), main);
        if (cases is not null) File.WriteAllText(Path.Combine(dir, "susu-plugin.test.json"), cases, new UTF8Encoding(false));
        return dir;
    }

    [Theory]
    [InlineData("not json at all", "not valid JSON")]
    [InlineData("""{ "cases": [] }""", "at least one case")]
    [InlineData("""{ "cases": [ { "name": "a", "request": {}, "expect": { "result": {} }, "bogus": 1 } ] }""", "$.cases[0].bogus: unknown key")]
    [InlineData("""{ "cases": [ { "request": {}, "expect": { "result": {} } } ] }""", "$.cases[0].name: required")]
    [InlineData("""{ "cases": [ { "name": "a", "request": {}, "expect": { "result": {}, "error": "auth" } } ] }""", "exactly one of")]
    [InlineData("""{ "cases": [ { "name": "a", "request": {}, "expect": { "error": "kaboom" } } ] }""", "not an error class")]
    [InlineData("""{ "cases": [ { "name": "a", "request": {}, "vendor": [ { "status": 99 } ], "expect": { "result": {} } } ] }""", "vendor[0].status")]
    [InlineData("""{ "cases": [ { "name": "a", "request": {}, "expect": { "result": {} } }, { "name": "a", "request": {}, "expect": { "result": {} } } ] }""", "duplicate case name")]
    [InlineData("""{ "cases": [ { "name": "a", "request": { "f": { "$file": "nope" } }, "expect": { "result": {} } } ] }""", "names no entry of \"inputs\"")]
    public void A_malformed_cases_file_is_a_usage_error_naming_the_problem(string cases, string expectedMessage)
    {
        if (Exes() is not var (cli, host)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string dir = Generated("translate", cases: cases);

        var test = Run(cli, "test", dir, "--host", host);
        Assert.True(test.Exit is 2 or 1, test.Out + test.Err);
        Assert.Contains(expectedMessage, test.Err + test.Out);
        if (!cases.Contains("$file")) Assert.Equal(2, test.Exit); // a bad file is never reported as a failing plugin

        var check = Run(cli, "check", dir);
        if (!cases.Contains("$file")) Assert.Equal(1, check.Exit); // `check` catches it without a sandbox
    }

    [Fact]
    public void A_wrong_expectation_fails_with_exit_1_and_says_what_differed()
    {
        if (Exes() is not var (cli, host)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string dir = Generated("translate", cases: """
            { "version": 1, "cases": [
              { "name": "wrong text", "request": { "text": "hello", "from": "en", "to": "zh-Hans" }, "secrets": { "apiKey": "k" },
                "vendor": [ { "status": 200, "json": { "translation": "你好" } } ], "expect": { "result": { "text": "再见" } } },
              { "name": "wrong class", "request": { "text": "hello" }, "secrets": { "apiKey": "k" },
                "vendor": [ { "status": 401, "json": {} } ], "expect": { "error": "quota" } },
              { "name": "wrong request count", "request": { "text": "hello" }, "secrets": { "apiKey": "k" },
                "vendor": [ { "status": 200, "json": { "translation": "x" } } ], "expectRequests": [ {}, {} ], "expect": { "result": { "text": "x" } } },
              { "name": "wrong request body", "request": { "text": "hello" }, "secrets": { "apiKey": "k" },
                "vendor": [ { "status": 200, "json": { "translation": "x" } } ], "expectRequests": [ { "bodyJson": { "q": "other" } } ], "expect": { "result": { "text": "x" } } },
              { "name": "right", "request": { "text": "hello" }, "secrets": { "apiKey": "k" },
                "vendor": [ { "status": 200, "json": { "translation": "x" } } ], "expect": { "result": { "text": "x" } } }
            ] }
            """);

        var test = Run(cli, "test", dir, "--host", host);
        Assert.Equal(1, test.Exit);
        Assert.Contains("4 failed", test.Out);
        Assert.Contains("1 passed", test.Out);
        Assert.Contains("result.text: expected \"再见\", got \"你好\"", test.Err);
        Assert.Contains("expected error 'quota', got 'auth'", test.Err);
        Assert.Contains("sent 1 vendor request(s), expected 2", test.Err);
        Assert.Contains("body.q: expected \"other\", got \"hello\"", test.Err);
    }

    [Fact]
    public void A_slow_vendor_times_out_and_a_hung_plugin_cannot_block_the_next_case()
    {
        if (Exes() is not var (cli, host)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string dir = Generated("translate", cases: """
            { "version": 1, "cases": [
              { "name": "slow vendor expected to time out", "request": { "text": "hello" }, "secrets": { "apiKey": "k" }, "timeoutMs": 500,
                "vendor": [ { "status": 200, "json": { "translation": "late" }, "delayMs": 5000 } ], "expect": { "error": "timeout" } },
              { "name": "slow vendor but a result was expected", "request": { "text": "hello" }, "secrets": { "apiKey": "k" }, "timeoutMs": 500,
                "vendor": [ { "status": 200, "json": { "translation": "late" }, "delayMs": 5000 } ], "expect": { "result": { "text": "late" } } },
              { "name": "next case still works", "request": { "text": "hello" }, "secrets": { "apiKey": "k" },
                "vendor": [ { "status": 200, "json": { "translation": "ok" } } ], "expect": { "result": { "text": "ok" } } }
            ] }
            """);

        var test = Run(cli, "test", dir, "--host", host);
        Assert.Equal(1, test.Exit);
        Assert.Contains("PASS  slow vendor expected to time out", test.Out);
        Assert.Contains("FAIL  slow vendor but a result was expected", test.Out);
        Assert.Contains("expected a result, got error 'timeout'", test.Err);
        Assert.Contains("PASS  next case still works", test.Out);
    }

    private const string Misbehaving = """
        export default { async translate(req, ctx) {
          if (req.text === 'hang') { while (true) {} }
          if (req.text === 'throw') throw new Error('boom');
          if (req.text === 'quota') throw new PluginError('quota', 'used up');
          if (req.text === 'oom') { const a = []; while (true) a.push(new Array(1000000).fill(1)); }
          if (req.text === 'deep') { const f = (n) => f(n + 1) + 1; return { text: String(f(0)) }; }
          return { text: 'ok' };
        } };
        """;

    [Fact]
    public void A_plugin_that_throws_runs_out_of_memory_or_never_returns_is_contained_and_classified()
    {
        if (Exes() is not var (cli, host)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string dir = Generated("translate", main: Misbehaving, cases: """
            { "version": 1, "cases": [
              { "name": "fine", "request": { "text": "fine" }, "expect": { "result": { "text": "ok" } } },
              { "name": "never returns", "request": { "text": "hang" }, "timeoutMs": 3000, "expect": { "error": "timeout" } },
              { "name": "after the hang", "request": { "text": "fine" }, "expect": { "result": { "text": "ok" } } },
              { "name": "throws a plain Error", "request": { "text": "throw" }, "expect": { "error": "bad_response", "detailContains": "boom" } },
              { "name": "throws PluginError quota", "request": { "text": "quota" }, "expect": { "error": "quota", "detailContains": "used up" } },
              { "name": "runs out of memory", "request": { "text": "oom" }, "timeoutMs": 30000, "expect": { "error": "bad_response", "detailContains": "memory" } },
              { "name": "runs out of stack", "request": { "text": "deep" }, "expect": { "error": "bad_response", "detailContains": "stack" } },
              { "name": "still alive afterwards", "request": { "text": "fine" }, "expect": { "result": { "text": "ok" } } }
            ] }
            """);

        var test = Run(cli, "test", dir, "--host", host);
        Assert.True(test.Exit == 0, test.Out + test.Err);
        Assert.Contains("8 passed, 0 failed", test.Out);
    }

    [Fact]
    public void A_case_calling_an_undeclared_capability_and_the_filter_option_behave()
    {
        if (Exes() is not var (cli, host)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string dir = Generated("translate", cases: """
            { "version": 1, "cases": [ { "name": "x", "capability": "ocr", "request": {}, "expect": { "result": {} } } ] }
            """);
        var check = Run(cli, "check", dir);
        Assert.Equal(1, check.Exit);
        Assert.Contains("does not declare", check.Err);
        Assert.Equal(2, Run(cli, "test", dir, "--host", host).Exit);

        string dir2 = Generated("translate");
        var filtered = Run(cli, "test", dir2, "--host", host, "--filter", "translates");
        Assert.Equal(0, filtered.Exit);
        Assert.Contains("1 case(s)", filtered.Out);
        Assert.Equal(2, Run(cli, "test", dir2, "--host", host, "--filter", "no such case").Exit);
    }

    [Fact]
    public void Test_without_a_cases_file_or_a_host_is_a_usage_error()
    {
        if (Exes() is not var (cli, host)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string dir = Generated("translate");
        File.Delete(Path.Combine(dir, "susu-plugin.test.json"));
        var none = Run(cli, "test", dir, "--host", host);
        Assert.Equal(2, none.Exit);
        Assert.Contains("not found", none.Err);

        string dir2 = Generated("translate");
        var noHost = Run(cli, "test", dir2, "--host", Path.Combine(dir2, "nope.exe"));
        Assert.Equal(2, noHost.Exit);
    }

    [Fact]
    public void A_package_whose_entry_file_is_missing_fails_check_and_test()
    {
        if (Exes() is not var (cli, host)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string dir = Generated("translate", main: """
            export default { async translate(req, ctx) { return { text: 'ok' }; } };
            """);
        string manifest = File.ReadAllText(Path.Combine(dir, "manifest.yaml")).Replace("entry: main.js", "entry: missing.js");
        File.WriteAllText(Path.Combine(dir, "manifest.yaml"), manifest);
        var check = Run(cli, "check", dir);
        Assert.Equal(1, check.Exit);
        Assert.Contains("entry file 'missing.js'", check.Err);
        Assert.Equal(1, Run(cli, "test", dir, "--host", host).Exit);
    }

    // ---- pack, keygen, signature ----

    [Fact]
    public void Pack_builds_an_installable_zip_without_author_files_and_a_signed_package_verifies_in_check()
    {
        if (Exes() is not var (cli, _)) { Assert.Skip("publish susu-plugin.exe and susu.exe first"); return; }
        string dir = Generated("translate");
        string root = Path.GetDirectoryName(dir)!;

        var unsigned = Run(cli, "pack", dir, "--out", Path.Combine(root, "plain.susuext"));
        Assert.True(unsigned.Exit == 0, unsigned.Err);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(root, "plain.susuext")))
        {
            var names = zip.Entries.Select(e => e.FullName).Order().ToList();
            Assert.Equal(["main.js", "manifest.yaml"], names); // the cases file stays with the author, no signature entry
        }

        string key = Path.Combine(root, "author.key");
        var keygen = Run(cli, "keygen", key);
        Assert.Equal(0, keygen.Exit);
        Assert.Equal(2, Run(cli, "keygen", key).Exit); // never overwrites a seed

        var signed = Run(cli, "pack", dir, "--out", Path.Combine(root, "signed.susuext"), "--key", key);
        Assert.True(signed.Exit == 0, signed.Err);
        string unpacked = Path.Combine(root, "unpacked");
        System.IO.Compression.ZipFile.ExtractToDirectory(Path.Combine(root, "signed.susuext"), unpacked);
        var verified = Run(cli, "check", unpacked);
        Assert.True(verified.Exit == 0, verified.Out + verified.Err);
        Assert.Contains("signature: valid", verified.Out);

        File.AppendAllText(Path.Combine(unpacked, "main.js"), "\n// tampered\n");
        var tampered = Run(cli, "check", unpacked);
        Assert.Equal(1, tampered.Exit);
        Assert.Contains("signature-invalid", tampered.Err);
    }
}
