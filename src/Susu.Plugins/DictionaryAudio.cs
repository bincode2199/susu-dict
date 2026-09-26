using Susu.Abstractions;
using Susu.Contracts;
using Susu.Net;
using Susu.Storage;

namespace Susu.Plugins;

/// <summary>Why a dictionary audio request was refused before or during the download (S06).</summary>
public enum AudioRefusal
{
    /// <summary>The id is unknown, belongs to an earlier generation, or its entry is no longer shown.</summary>
    UnknownOrStale,
    /// <summary>The link is not an absolute http(s) URL without user info.</summary>
    InvalidLink,
    /// <summary>The link's origin is not one the source provider declared.</summary>
    OriginNotDeclared,
    /// <summary>A redirect left the source provider's declared origins.</summary>
    RedirectOutsidePolicy,
}

public abstract record DictionaryAudioOutcome
{
    /// <summary>
    /// The audio is in a host cache file owned by <paramref name="Lease"/> (ARCHITECTURE 8.4). The consumer gets its
    /// path with <see cref="FileLeases.PathOf"/> and must call <see cref="FileLeases.Release"/> when done; the file is
    /// deleted at the last release, and at the latest when the session ends.
    /// </summary>
    public sealed record Ready(FileLease Lease, string Mime, long Length) : DictionaryAudioOutcome;
    public sealed record Refused(AudioRefusal Reason) : DictionaryAudioOutcome;
    public sealed record Failure(ErrorKind Kind) : DictionaryAudioOutcome;
}

/// <summary>The host port F10 consumes to play a dictionary entry's real-voice audio (ARCHITECTURE 7).</summary>
public interface IDictionaryAudioSource
{
    Task<DictionaryAudioOutcome> FetchAsync(string audioId, CancellationToken cancellationToken);
}

/// <summary>
/// F09.3 audio-link authorization (PLAN 4.5.4 item 3, S06, DICT03). An audio id resolves through the translation
/// session only while its entry is still shown; the link may be downloaded only within the origins the source
/// provider declared (its manifest <c>hosts</c>, the same grant its calls get). The download is one
/// uncredentialed GET through the shared <see cref="NetworkBroker"/> (DNS pinning, private-address block, proxy,
/// size limit), and every redirect hop must stay inside the same declared origins. The UI never loads the URL.
/// </summary>
public sealed class DictionaryAudioFetcher(
    Func<string, Task<DictionaryAudioLink?>> resolve,
    Func<string, IReadOnlyList<string>?> declaredOrigins,
    Func<NetworkBroker> network,
    FileLeases leases) : IDictionaryAudioSource
{
    public async Task<DictionaryAudioOutcome> FetchAsync(string audioId, CancellationToken cancellationToken)
    {
        if (await resolve(audioId) is not { } link) return new DictionaryAudioOutcome.Refused(AudioRefusal.UnknownOrStale);
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
            return new DictionaryAudioOutcome.Refused(AudioRefusal.InvalidLink);
        var allowed = AllowedOrigins(declaredOrigins(link.ServiceId));
        if (!allowed.Contains(NetworkBroker.Origin(uri))) return new DictionaryAudioOutcome.Refused(AudioRefusal.OriginNotDeclared);

        var request = new BrokerHttpRequest("GET", uri, [], RequestBody.None, [], [], (_, _) => throw new InvalidOperationException("dictionary audio carries no credentials"),
            null, ResponseKind.File, [], null, LocalOriginApproved: true, AllowedOrigins: allowed);
        BrokerOutcome outcome;
        try { outcome = await network().ExecuteAsync(request, cancellationToken); }
        catch (OperationCanceledException) { return new DictionaryAudioOutcome.Failure(ErrorKind.Cancelled); }
        switch (outcome)
        {
            case BrokerFailure { Kind: "forbidden" }: return new DictionaryAudioOutcome.Refused(AudioRefusal.RedirectOutsidePolicy);
            case BrokerFailure failure: return new DictionaryAudioOutcome.Failure(failure.Kind == "timeout" ? ErrorKind.Timeout : failure.Kind == "network" ? ErrorKind.Network : ErrorKind.BadResponse);
            case BrokerSuccess { Response: var response }:
                if (response.Status is < 200 or > 299 || response.RawFile is not { Length: > 0 } bytes)
                    return new DictionaryAudioOutcome.Failure(response.Status is 429 ? ErrorKind.RateLimited : response.Status is >= 500 ? ErrorKind.Network : ErrorKind.BadResponse);
                if (ClassifyAudio(response.RawFileMime, bytes) is not { } kept) return new DictionaryAudioOutcome.Failure(ErrorKind.BadResponse);
                var (mime, extension) = kept;
                var lease = leases.Create("dictionary-audio", extension);
                try { await File.WriteAllBytesAsync(leases.PathOf(lease), bytes, cancellationToken); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    leases.Release(lease);
                    return new DictionaryAudioOutcome.Failure(error is OperationCanceledException ? ErrorKind.Cancelled : ErrorKind.Unavailable);
                }
                return new DictionaryAudioOutcome.Ready(lease, mime, bytes.LongLength);
            default: return new DictionaryAudioOutcome.Failure(ErrorKind.BadResponse);
        }
    }

    /// <summary>Declared origins normalized to <see cref="NetworkBroker.Origin"/> form (explicit port); invalid entries are dropped.</summary>
    public static HashSet<string> AllowedOrigins(IReadOnlyList<string>? declared)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var origin in declared ?? [])
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") set.Add(NetworkBroker.Origin(uri));
        return set;
    }

    /// <summary>Only audio content is kept (a JSON/HTML error page served as 200 is a bad response, never played).</summary>
    public static string? ExtensionFor(string? mime) => mime?.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => "mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
        "audio/ogg" => "ogg",
        "audio/aac" => "aac",
        "audio/mp4" or "audio/x-m4a" => "m4a",
        "audio/webm" => "webm",
        "audio/flac" or "audio/x-flac" => "flac",
        _ => null,
    };

    /// <summary>
    /// The MIME and extension to keep the download under, or null to refuse it. An audio Content-Type is taken as
    /// is. <c>application/octet-stream</c> (which the broker also reports for a missing Content-Type) is kept only
    /// when the body's magic bytes identify an audio container, and is then stored under that container's audio
    /// MIME; anything else (an HTML/JSON page served as octet-stream) is refused (F09 decision).
    /// </summary>
    public static (string Mime, string Extension)? ClassifyAudio(string? mime, ReadOnlySpan<byte> body)
    {
        if (ExtensionFor(mime) is { } extension) return (mime!.ToLowerInvariant(), extension);
        return string.Equals(mime, "application/octet-stream", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(mime) ? SniffAudio(body) : null;
    }

    /// <summary>Identifies MP3 (ID3 tag or MPEG audio frame sync), WAV (RIFF/WAVE), OGG, FLAC, AAC ADTS and MP4/M4A (ftyp).</summary>
    public static (string Mime, string Extension)? SniffAudio(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith("ID3"u8)) return ("audio/mpeg", "mp3");
        if (b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WAVE"u8)) return ("audio/wav", "wav");
        if (b.StartsWith("OggS"u8)) return ("audio/ogg", "ogg");
        if (b.StartsWith("fLaC"u8)) return ("audio/flac", "flac");
        if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8)) return ("audio/mp4", "m4a");
        if (b.Length >= 2 && b[0] == 0xFF)
        {
            // AAC ADTS: 12-bit sync, layer bits 00. MPEG audio: 11-bit sync, layer bits non-zero (01/10/11).
            if ((b[1] & 0xF6) == 0xF0) return ("audio/aac", "aac");
            if ((b[1] & 0xE0) == 0xE0 && (b[1] & 0x06) != 0 && (b[1] & 0x18) != 0x08) return ("audio/mpeg", "mp3");
        }
        return null;
    }
}
