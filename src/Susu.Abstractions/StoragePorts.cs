using Susu.Domain;

namespace Susu.Abstractions;

/// <summary>Encrypts secrets for the current Windows user (DPAPI in production; never machine scope).</summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

/// <summary>Test seam for crash/disk-full injection at named commit stages (DATA02). Production passes none.</summary>
public interface IFaultPoint
{
    void Hit(string stage);
}

/// <summary>A problem in a user-editable file, reported with its exact path (DATA01).</summary>
public sealed record SettingsIssue(string Path, string Code, string Message, int Line, int Column);

/// <summary>
/// Effective settings and the state of settings.yaml. When the file is invalid, <see cref="Effective"/> is the
/// last valid state and <see cref="Issues"/> describes the file; the invalid file is never overwritten with defaults.
/// </summary>
public sealed record SettingsState(AppSettings Effective, long Revision, string FileHash, IReadOnlyList<SettingsIssue> Issues)
{
    public bool FileInvalid => Issues.Count > 0;
}

public enum SaveStatus { Saved, Conflict, Invalid, Failed }

public sealed record SaveResult(SaveStatus Status, SettingsState State, IReadOnlyList<SettingsIssue> Issues, string? Error = null);

public interface ISettingsStore
{
    SettingsState State { get; }
    event Action<SettingsState>? Changed;
    /// <summary>Saves only if the caller's view (revision + file hash) is still current; otherwise <see cref="SaveStatus.Conflict"/>.</summary>
    SaveResult Save(AppSettings proposed, long expectedRevision, string expectedFileHash);
}

/// <summary>
/// Write-only credential store from the UI's point of view (PLAN 5.4): values go in, only presence comes out.
/// Plaintext is released to the network layer through <see cref="TryRead"/> after authorization.
/// </summary>
public interface ISecretStore
{
    bool Has(string accountId, string secretName);
    IReadOnlyList<string> Names(string accountId);
    void Write(string accountId, string secretName, ReadOnlySpan<char> value);
    bool Delete(string accountId, string secretName);
    bool TryRead(string accountId, string secretName, out string value);
}

public sealed record WindowPlacement(string WindowKind, string MonitorHint, int X, int Y, int Dpi);

public interface IWindowStateStore
{
    WindowPlacement? Get(string windowKind);
    void Save(WindowPlacement placement);
}

/// <summary>Plugin <c>$store</c> backed by plugin_kv, keyed by package id (F16.2); the id and namespace are bound by the host, never chosen by the plugin.</summary>
public interface IPluginKv
{
    string? Get(string packageId, string key);
    void Set(string packageId, string key, string valueJson);
    bool Delete(string packageId, string key);
}
