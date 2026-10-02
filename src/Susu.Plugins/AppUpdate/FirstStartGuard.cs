using System.Text.Json;
using System.Text.Json.Serialization;
using Susu.Storage;

namespace Susu.Plugins.AppUpdate;

public sealed record FirstStartState(string Version, string FromVersion, int Attempts, bool Confirmed);

public sealed record FailedVersionsFile(string[] Versions);

public enum StartDecision { Proceed, Fallback }

/// <summary>
/// F18.3: the first start of a freshly committed version. The commit arms it (version, not confirmed, 0 attempts). Each start of that version counts an attempt BEFORE
/// anything else runs; the app confirms once start-up has finished (database open, shell running). A start that finds <see cref="MaxAttempts"/> unconfirmed attempts
/// does not run: the caller starts the helper in rollback mode, which restores the paired old binaries and database and records the version as failed. That record is the
/// loop guard: the same version is never offered, downloaded or installed again (<see cref="IsFailed"/>), so the app cannot flip between the two. All files are atomic
/// replaces; a damaged state file is treated as "no guard" for the confirm step and as "armed with the attempts used up" for the start step only when the file exists
/// but cannot be read (so a corrupted state never hides a crash loop).
/// </summary>
public sealed class FirstStartGuard(string updatesFolder, int maxAttempts = 2)
{
    public const int DefaultMaxAttempts = 2;
    public int MaxAttempts => maxAttempts;
    private string StatePath => Path.Combine(updatesFolder, "first-start.json");
    private string FailedPath => Path.Combine(updatesFolder, "failed-versions.json");

    public FirstStartState? Read()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            return JsonSerializer.Deserialize(File.ReadAllBytes(StatePath), FirstStartJson.Default.FirstStartState);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new FirstStartState("?", "?", int.MaxValue / 2, false); }
    }

    public void Arm(string toVersion, string fromVersion)
    {
        Directory.CreateDirectory(updatesFolder);
        AtomicFile.Write(StatePath, JsonSerializer.SerializeToUtf8Bytes(new FirstStartState(toVersion, fromVersion, 0, false), FirstStartJson.Default.FirstStartState));
    }

    public void Clear()
    {
        try { File.Delete(StatePath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Counts this start. A state for another version (the old version running after a fallback) or a confirmed one is removed or ignored and the start proceeds.</summary>
    public StartDecision OnStart(string runningVersion)
    {
        var state = Read();
        if (state is null) return StartDecision.Proceed;
        if (state.Version != "?" && (state.Version != runningVersion || state.Confirmed))
        {
            if (state.Version != runningVersion) Clear(); // not the armed version any more: stale
            return StartDecision.Proceed;
        }
        if (state.Attempts >= maxAttempts) return StartDecision.Fallback;
        try { AtomicFile.Write(StatePath, JsonSerializer.SerializeToUtf8Bytes(state with { Attempts = state.Attempts + 1 }, FirstStartJson.Default.FirstStartState)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return StartDecision.Fallback; } // cannot count: never run an uncounted start
        return StartDecision.Proceed;
    }

    /// <summary>Start-up finished: the armed version works on this machine; the guard is released.</summary>
    public void Confirm(string runningVersion)
    {
        var state = Read();
        if (state is not null && state.Version == runningVersion) Clear();
    }

    public bool IsFailed(string version) => FailedVersions().Contains(version, StringComparer.Ordinal);

    public string[] FailedVersions()
    {
        try
        {
            if (!File.Exists(FailedPath)) return [];
            return JsonSerializer.Deserialize(File.ReadAllBytes(FailedPath), FirstStartJson.Default.FailedVersionsFile)?.Versions ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public void MarkFailed(string version)
    {
        if (IsFailed(version)) return;
        Directory.CreateDirectory(updatesFolder);
        AtomicFile.Write(FailedPath, JsonSerializer.SerializeToUtf8Bytes(new FailedVersionsFile([.. FailedVersions(), version]), FirstStartJson.Default.FailedVersionsFile));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FirstStartState))]
[JsonSerializable(typeof(FailedVersionsFile))]
internal sealed partial class FirstStartJson : JsonSerializerContext;
