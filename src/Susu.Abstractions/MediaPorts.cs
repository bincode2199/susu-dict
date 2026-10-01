namespace Susu.Abstractions;

/// <summary>
/// Error codes of the media decode path (F14.1, ARCHITECTURE 7 <c>IMediaDecoder</c>, TEST-PLAN VID01/VID02). Every code has its
/// own exit so the UI never has to guess from an exception type.
/// </summary>
public static class MediaErrors
{
    /// <summary>The token is unknown or was revoked (the picker/drop was never done, or the job was closed).</summary>
    public const string UnknownToken = "media.unknownToken";
    public const string NotFound = "media.notFound";
    /// <summary>The container or every audio codec in it cannot be decoded on this machine (for example an N edition without the codec).</summary>
    public const string UnsupportedEncoding = "media.unsupportedEncoding";
    /// <summary>The file opens as media but has no audio stream.</summary>
    public const string NoAudio = "media.noAudio";
    /// <summary>The file is damaged: it failed to open as media, or decoding failed in the middle.</summary>
    public const string Corrupt = "media.corrupt";
    public const string OpenFailed = "media.openFailed";
    /// <summary>The temp slice could not be written because the disk is full. Recoverable: retry after space was freed.</summary>
    public const string DiskFull = "media.diskFull";
    public const string WriteFailed = "media.writeFailed";
}

public sealed class MediaDecodeException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;
}

/// <summary>One audio stream of a media file. <see cref="Decodable"/> is true when a decoder to PCM exists on this machine.</summary>
public sealed record MediaAudioStream(int Index, string Codec, int SampleRate, int Channels, bool Decodable);

/// <summary>What a probe found (no audio is decoded). <see cref="Selected"/> is the first decodable audio stream, null when there is none.</summary>
public sealed record MediaProbe(TimeSpan Duration, bool HasVideo, IReadOnlyList<MediaAudioStream> AudioStreams)
{
    public MediaAudioStream? Selected => AudioStreams.FirstOrDefault(s => s.Decodable);
}

/// <summary>A run of 16 kHz mono 16-bit little-endian PCM with the container timestamp of its first sample.</summary>
public readonly record struct MediaPcmBlock(byte[] Pcm, TimeSpan Timestamp);

/// <summary>An opened media file decoding its selected audio stream to 16 kHz mono 16-bit PCM. Dispose releases the file.</summary>
public interface IMediaAudioReader : IDisposable
{
    MediaProbe Probe { get; }
    /// <summary>The next block, null at the end of the stream. Throws <see cref="MediaDecodeException"/> (media.corrupt) when decoding fails; honours the token.</summary>
    Task<MediaPcmBlock?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The Media Foundation layer (thin; tests use a fake). Opens by host path, which only host code ever holds. Throws
/// <see cref="MediaDecodeException"/> with media.notFound, media.unsupportedEncoding, media.noAudio, media.corrupt or media.openFailed.
/// </summary>
public interface IMediaDecoder
{
    /// <summary>
    /// Duration, streams and codecs without decoding. Does not throw for "no audio" or "no decodable audio": the probe says so
    /// (<see cref="MediaProbe.AudioStreams"/> empty or <see cref="MediaProbe.Selected"/> null) so the UI can explain before upload.
    /// </summary>
    Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken);
    Task<IMediaAudioReader> OpenAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// The host-side table from opaque tokens to file paths (ARCHITECTURE 6: the UI and plugins never see a real path). The picker and
/// drag-in issue tokens; only host decode code resolves them.
/// </summary>
public interface IMediaTokens
{
    /// <summary>Registers an existing file and returns its token, or null when the path is not an existing file.</summary>
    string? Issue(string path);
    bool TryResolve(string token, out string path);
    void Revoke(string token);
    /// <summary>The file name for display (no directory).</summary>
    string? DisplayName(string token);
}

/// <summary>The file open dialog. Returns the token of the chosen file, or null when the user cancelled.</summary>
public interface IMediaPicker
{
    Task<string?> PickAsync(CancellationToken cancellationToken);
}
