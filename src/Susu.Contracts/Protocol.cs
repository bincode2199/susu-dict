namespace Susu.Contracts;

/// <summary>Protocol versions (ARCHITECTURE 6). Each is negotiated independently.</summary>
public static class ProtocolVersions
{
    /// <summary>Main process ↔ plugin host IPC.</summary>
    public const int Ipc = 1;
    /// <summary>Main process ↔ WebView UI bridge (<c>uiVersion</c>).</summary>
    public const int Ui = 1;
    /// <summary>Plugin manifest <c>apiVersion</c> / <c>susu-plugin.d.ts</c>.</summary>
    public const int PluginApi = 1;

    public static readonly VersionRange SupportedIpc = new(1, 1);
    public static readonly VersionRange SupportedUi = new(1, 1);
    public static readonly VersionRange SupportedPluginApi = new(1, 1);
}

public readonly record struct VersionRange(int Min, int Max)
{
    public bool Contains(int version) => version >= Min && version <= Max;
    public override string ToString() => Min == Max ? $"{Min}" : $"{Min}..{Max}";
}

/// <summary>Bounded sizes and concurrency from PLAN 4.5.1 / 4.5.4 and ARCHITECTURE 5.1 / 6.</summary>
public static class ProtocolLimits
{
    public const int MaxFrameBytes = 1024 * 1024;
    public const int MaxStreamChunkBytes = 64 * 1024;
    public const int MaxReassembledJsonBytes = 4 * 1024 * 1024;
    public const int MaxUnackedStreamBytesPerCall = 256 * 1024;
    public const int MaxUnackedStreamBytesPerProcess = 4 * 1024 * 1024;
    public const int MaxHostOperationsPerCall = 4;
    public const int MaxHostOperationsPerProcess = 32;
    public const int MaxPluginLogBytes = 4 * 1024;
    public const int MaxPluginLogsPerSecond = 20;
    public const int MaxHttpErrorBodyBytes = 16 * 1024;
    public const int MaxBinaryBytes = 32 * 1024 * 1024;
    public const int MaxBase64JsonBytes = 48 * 1024 * 1024;
    public const int MaxInFlightCallsPerRuntime = 2;
    public const int UiStreamUpdatesPerSecond = 30;
}

/// <summary>Result of a version/identity handshake.</summary>
public sealed record NegotiationResult(bool Accepted, int Version, string? Reason)
{
    public static NegotiationResult Accept(int version) => new(true, version, null);
    public static NegotiationResult Reject(string reason) => new(false, 0, reason);
}

public static class ProtocolNegotiation
{
    /// <summary>
    /// Accepts a peer only when it speaks a supported version, has the expected role and was built from
    /// the same host build (the plugin host is the same executable; a stale child after an update is rejected).
    /// </summary>
    public static NegotiationResult Negotiate(VersionRange local, int remoteVersion, string expectedRole, string remoteRole, string localBuild, string remoteBuild)
    {
        if (!local.Contains(remoteVersion)) return NegotiationResult.Reject($"protocol version {remoteVersion} not in supported range {local}");
        if (!string.Equals(expectedRole, remoteRole, StringComparison.Ordinal)) return NegotiationResult.Reject($"unexpected process role '{remoteRole}'");
        if (!string.Equals(localBuild, remoteBuild, StringComparison.Ordinal)) return NegotiationResult.Reject("host build mismatch");
        return NegotiationResult.Accept(remoteVersion);
    }
}
