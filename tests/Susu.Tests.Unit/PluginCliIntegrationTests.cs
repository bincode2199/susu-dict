using System.Diagnostics;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F04.4: `susu-plugin check/test` against the real runtime channel (DEV-PLAN explicitly forbids
/// faking this with a Node runtime). Shells out to the published susu-plugin.exe and susu.exe, so it
/// only runs once both are published (`dotnet publish -c Release -r win-x64`), same as
/// PluginHostIntegrationTests.
/// </summary>
public class PluginCliIntegrationTests
{
    private static string? FindPublished(string relativeExe)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relativeExe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static (int ExitCode, string Output, string Error) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);
        return (process.ExitCode, output, error);
    }

    [Fact]
    public void Check_passes_for_a_valid_manifest_and_reports_its_capabilities()
    {
        string? cli = FindPublished(Path.Combine("tools", "Susu.PluginCli", "bin", "Release", "net10.0", "win-x64", "publish", "susu-plugin.exe"));
        if (cli is null) return; // build/publish susu-plugin.exe first
        string dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins", "echo");

        var (exitCode, output, error) = Run(cli, "check", dir);
        Assert.Equal(0, exitCode);
        Assert.Contains("com.example.echo", output);
        Assert.Contains("translate", output);
        Assert.Equal("", error.Trim());
    }

    [Fact]
    public void Check_fails_for_a_package_with_no_manifest()
    {
        string? cli = FindPublished(Path.Combine("tools", "Susu.PluginCli", "bin", "Release", "net10.0", "win-x64", "publish", "susu-plugin.exe"));
        if (cli is null) return;
        string dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins", "second"); // no manifest.yaml fixture

        var (exitCode, _, error) = Run(cli, "check", dir);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("missing", error);
    }

    [Fact]
    public void Test_loads_the_package_in_the_real_sandbox_and_invokes_its_capability()
    {
        string? cli = FindPublished(Path.Combine("tools", "Susu.PluginCli", "bin", "Release", "net10.0", "win-x64", "publish", "susu-plugin.exe"));
        string? host = FindPublished(Path.Combine("src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish", "susu.exe"));
        if (cli is null || host is null) return;
        string dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins", "echo");

        var (exitCode, output, error) = Run(cli, "test", dir, "--host", host, "--request", "{\"text\":\"cli-test\"}");
        Assert.True(exitCode == 0, $"exit={exitCode} out={output} err={error}");
        Assert.Contains("echo:cli-test", output);
    }

    /// <summary>F06 plugin contracts: the four shipped translation packages, as published next to susu.exe.</summary>
    [Theory]
    [InlineData("mymemory", "app.susu.mymemory")]
    [InlineData("openai", "app.susu.openai")]
    [InlineData("tencent-translate", "app.susu.tencent-translate")]
    [InlineData("deepl", "app.susu.deepl")]
    [InlineData("youdao", "app.susu.youdao")] // F09 P-T07
    public void Check_passes_for_every_shipped_translation_package(string directory, string id)
    {
        string? cli = FindPublished(Path.Combine("tools", "Susu.PluginCli", "bin", "Release", "net10.0", "win-x64", "publish", "susu-plugin.exe"));
        string? host = FindPublished(Path.Combine("src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish", "susu.exe"));
        if (cli is null || host is null) return;
        string dir = Path.Combine(Path.GetDirectoryName(host)!, "plugins", directory);

        var (exitCode, output, error) = Run(cli, "check", dir);
        Assert.True(exitCode == 0, $"exit={exitCode} out={output} err={error}");
        Assert.Contains($"check: ok - {id} v1, capabilities: translate", output);
    }

    /// <summary>
    /// F06 plugin contracts: `susu-plugin test` on a shipped package loads it and gets a classified answer
    /// (ok for keyless MyMemory; a plugin error for the keyed ones, which the CLI gives no key), never a crash.
    /// </summary>
    [Theory]
    [InlineData("mymemory")]
    [InlineData("openai")]
    [InlineData("tencent-translate")]
    [InlineData("deepl")]
    [InlineData("youdao")] // F09 P-T07
    public void Test_runs_every_shipped_translation_package_without_crashing(string directory)
    {
        string? cli = FindPublished(Path.Combine("tools", "Susu.PluginCli", "bin", "Release", "net10.0", "win-x64", "publish", "susu-plugin.exe"));
        string? host = FindPublished(Path.Combine("src", "Susu.Host", "bin", "Release", "net10.0", "win-x64", "publish", "susu.exe"));
        if (cli is null || host is null) return;
        string dir = Path.Combine(Path.GetDirectoryName(host)!, "plugins", directory);

        var (exitCode, output, error) = Run(cli, "test", dir, "--host", host, "--capability", "translate", "--request", "{\"text\":\"hello\",\"from\":\"en\",\"to\":\"zh-Hans\"}");
        Assert.DoesNotContain("Unhandled exception", error);
        Assert.True(exitCode is 0 or 1, $"exit={exitCode} out={output} err={error}");
        Assert.Contains("test: translate -> ", output + error);
    }
}
