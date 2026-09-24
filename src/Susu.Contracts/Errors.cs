using System.Text.Json.Serialization;

namespace Susu.Contracts;

/// <summary>
/// Error classes shared by providers, jobs and UI (PLAN 4.4). The first seven are the only kinds a
/// plugin may throw; the rest are produced by the host. Any other plugin error is <see cref="BadResponse"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ErrorKind>))]
public enum ErrorKind
{
    [JsonStringEnumMemberName("auth")] Auth,
    [JsonStringEnumMemberName("quota")] Quota,
    [JsonStringEnumMemberName("rate_limited")] RateLimited,
    [JsonStringEnumMemberName("network")] Network,
    [JsonStringEnumMemberName("timeout")] Timeout,
    [JsonStringEnumMemberName("unsupported_language")] UnsupportedLanguage,
    [JsonStringEnumMemberName("bad_response")] BadResponse,
    /// <summary>Host: the caller cancelled (never shown as a service failure).</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    /// <summary>Host: bounded queue full; the request was not accepted.</summary>
    [JsonStringEnumMemberName("busy")] Busy,
    /// <summary>Host: provider/runtime unavailable (disabled, crashed and not restarted, missing).</summary>
    [JsonStringEnumMemberName("unavailable")] Unavailable,
}

public static class ErrorKinds
{
    private static readonly Dictionary<string, ErrorKind> pluginKinds = new(StringComparer.Ordinal)
    {
        ["auth"] = ErrorKind.Auth, ["quota"] = ErrorKind.Quota, ["rate_limited"] = ErrorKind.RateLimited,
        ["network"] = ErrorKind.Network, ["timeout"] = ErrorKind.Timeout,
        ["unsupported_language"] = ErrorKind.UnsupportedLanguage, ["bad_response"] = ErrorKind.BadResponse,
    };

    /// <summary>Maps a plugin-thrown kind; anything outside the published list is bad_response.</summary>
    public static ErrorKind FromPlugin(string? kind) => kind is not null && pluginKinds.TryGetValue(kind, out var value) ? value : ErrorKind.BadResponse;

    public static bool IsPluginVisible(ErrorKind kind) => kind <= ErrorKind.BadResponse;
}

/// <summary>A classified failure. <see cref="Detail"/> is diagnostic only and never user-facing text.</summary>
public sealed record ProviderError(ErrorKind Kind, string? Detail = null, TimeSpan? RetryAfter = null);
