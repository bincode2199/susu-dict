using System.Collections.Concurrent;
using System.Threading.Channels;
using Susu.Abstractions;

namespace Susu.Jobs;

/// <summary>The in-memory token table (F14.1). Tokens are random and opaque; the path never leaves host code.</summary>
public sealed class MediaTokens : IMediaTokens
{
    private readonly ConcurrentDictionary<string, string> paths = new();

    public string? Issue(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) return null;
            string token = "media-" + Guid.NewGuid().ToString("N");
            paths[token] = full;
            return token;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException) { return null; }
    }

    public bool TryResolve(string token, out string path) => paths.TryGetValue(token, out path!);

    public void Revoke(string token) => paths.TryRemove(token, out _);

    public string? DisplayName(string token) => paths.TryGetValue(token, out string? p) ? Path.GetFileName(p) : null;
}

/// <param name="SliceLength">Audio length of one temp WAV slice (the ASR chunk planner still splits inside a slice by the model's limits).</param>
/// <param name="MaxAhead">Slices that may exist (decoded, not yet released by the consumer) at once: ARCHITECTURE 8 prefetches at most 2.</param>
/// <param name="OpenWrite">Opens the slice file for writing (tests inject disk-full); default is a plain FileStream.</param>
public sealed record MediaSliceOptions(TimeSpan SliceLength, int MaxAhead = 2, Func<string, Stream>? OpenWrite = null)
{
    public static MediaSliceOptions Default { get; } = new(TimeSpan.FromMinutes(5));
}

/// <summary>
/// One decoded slice: a 16 kHz mono 16-bit WAV lease at <see cref="Start"/> on the media time axis. Disposing releases the lease (the
/// file goes) and lets the decoder start the next slice: that is the back-pressure.
/// </summary>
public sealed class MediaSlice : IDisposable
{
    private readonly MediaSession owner;
    private int disposed;

    internal MediaSlice(MediaSession owner, int index, TimeSpan start, TimeSpan duration, ILeasedFile wav)
    {
        this.owner = owner; Index = index; Start = start; Duration = duration; Wav = wav;
    }

    public int Index { get; }
    public TimeSpan Start { get; }
    public TimeSpan Duration { get; }
    public ILeasedFile Wav { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Wav.Dispose();
        owner.Released(this);
    }
}

public abstract record MediaStep
{
    public sealed record Slice(MediaSlice Value) : MediaStep;
    /// <summary>All audio was decoded and handed out.</summary>
    public sealed record End(int Slices, TimeSpan Audio) : MediaStep;
    /// <summary>
    /// The decode stopped. <see cref="Retryable"/> (disk full) means <see cref="MediaSession.Retry"/> re-attempts the same slice from the
    /// samples already decoded; otherwise the session is over.
    /// </summary>
    public sealed record Failure(string Code, string Detail, bool Retryable) : MediaStep;
}

/// <summary>
/// F14.1 decoder front end: turns a mediaToken into rolling temp WAV slices for the ASR pipeline (ARCHITECTURE 7 IMediaDecoder, 8).
/// </summary>
public sealed class MediaSlicer(IMediaTokens tokens, IMediaDecoder decoder, ILeasedFileFactory files, MediaSliceOptions? options = null)
{
    public async Task<MediaSession> OpenAsync(string token, CancellationToken cancellationToken)
    {
        if (!tokens.TryResolve(token, out string path)) throw new MediaDecodeException(MediaErrors.UnknownToken, "unknown media token");
        var reader = await decoder.OpenAsync(path, cancellationToken);
        return new MediaSession(reader, files, options ?? MediaSliceOptions.Default, cancellationToken);
    }
}

public sealed class MediaSession : IDisposable
{
    public const int Rate = 16000;
    private readonly IMediaAudioReader reader;
    private readonly ILeasedFileFactory files;
    private readonly MediaSliceOptions options;
    private readonly SemaphoreSlim permits;
    private readonly Channel<MediaStep> steps = Channel.CreateUnbounded<MediaStep>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource cts;
    private readonly HashSet<MediaSlice> outstanding = [];
    private readonly Task producer;
    private TaskCompletionSource? retry;
    private MediaStep? terminal;
    private int disposed;

    internal MediaSession(IMediaAudioReader reader, ILeasedFileFactory files, MediaSliceOptions options, CancellationToken cancellationToken)
    {
        this.reader = reader; this.files = files; this.options = options;
        permits = new SemaphoreSlim(Math.Max(1, options.MaxAhead));
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        producer = Task.Run(() => Run(cts.Token));
    }

    public MediaProbe Probe => reader.Probe;

    /// <summary>The next slice, the end, or a failure. After a terminal step the same step is returned again.</summary>
    public async Task<MediaStep> NextAsync(CancellationToken cancellationToken)
    {
        if (terminal is not null) return terminal;
        while (true)
        {
            if (!await steps.Reader.WaitToReadAsync(cancellationToken))
                return terminal = new MediaStep.Failure("media.cancelled", "decode stopped", false);
            if (!steps.Reader.TryRead(out var step)) continue;
            if (step is MediaStep.End or MediaStep.Failure { Retryable: false }) terminal = step;
            return step;
        }
    }

    /// <summary>Re-attempts the write that failed with a retryable error (call after the user freed disk space).</summary>
    public void Retry() => Interlocked.Exchange(ref retry, null)?.TrySetResult();

    internal void Released(MediaSlice slice)
    {
        lock (outstanding) outstanding.Remove(slice);
        permits.Release();
    }

    private async Task Run(CancellationToken ct)
    {
        try
        {
            long startSamples = 0;
            int index = 0;
            bool end = false;
            byte[] carry = [];
            int carryOffset = 0;
            long gap = 0;
            int capacity = (int)(options.SliceLength.TotalSeconds * Rate) * 2;
            while (!end)
            {
                await permits.WaitAsync(ct);
                bool held = true;
                try
                {
                    var buffer = new byte[capacity];
                    int length = 0;
                    while (length < capacity)
                    {
                        if (gap > 0)
                        {
                            int n = (int)Math.Min(gap * 2, capacity - length) & ~1; // silence keeps the time axis where the container put the samples
                            if (n == 0) break;
                            length += n; gap -= n / 2; // buffer is zero-filled
                        }
                        else if (carryOffset < carry.Length)
                        {
                            int n = Math.Min(carry.Length - carryOffset, capacity - length);
                            Buffer.BlockCopy(carry, carryOffset, buffer, length, n);
                            carryOffset += n; length += n;
                        }
                        else
                        {
                            var block = await reader.ReadAsync(ct);
                            if (block is not { } b) { end = true; break; }
                            long position = startSamples + length / 2;
                            long stamp = (long)(b.Timestamp.Ticks * (double)Rate / TimeSpan.TicksPerSecond);
                            if (stamp - position > Rate / 5) gap = stamp - position; // gaps up to 200 ms are decoder jitter
                            carry = b.Pcm.Length % 2 == 0 ? b.Pcm : b.Pcm[..^1];
                            carryOffset = 0;
                        }
                    }
                    if (length == 0) break;
                    ILeasedFile? wav = null;
                    while (wav is null)
                    {
                        try { wav = WriteSlice(buffer, length); }
                        catch (IOException e)
                        {
                            bool full = IsDiskFull(e);
                            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            if (full) retry = wait;
                            steps.Writer.TryWrite(new MediaStep.Failure(full ? MediaErrors.DiskFull : MediaErrors.WriteFailed, e.Message, full));
                            if (!full) return;
                            await wait.Task.WaitAsync(ct);
                        }
                        catch (UnauthorizedAccessException e)
                        {
                            steps.Writer.TryWrite(new MediaStep.Failure(MediaErrors.WriteFailed, e.Message, false));
                            return;
                        }
                    }
                    var slice = new MediaSlice(this, index++, TimeSpan.FromSeconds((double)startSamples / Rate), TimeSpan.FromSeconds((double)(length / 2) / Rate), wav);
                    startSamples += length / 2;
                    lock (outstanding) outstanding.Add(slice);
                    held = false; // the slice now owns the permit
                    steps.Writer.TryWrite(new MediaStep.Slice(slice));
                }
                finally { if (held) permits.Release(); }
            }
            steps.Writer.TryWrite(index == 0
                ? new MediaStep.Failure(MediaErrors.NoAudio, "the file has no decodable audio", false)
                : new MediaStep.End(index, TimeSpan.FromSeconds((double)startSamples / Rate)));
        }
        catch (OperationCanceledException) { }
        catch (MediaDecodeException e) { steps.Writer.TryWrite(new MediaStep.Failure(e.Code, e.Message, false)); }
        catch (Exception e) { steps.Writer.TryWrite(new MediaStep.Failure(MediaErrors.Corrupt, e.GetType().Name + ": " + e.Message, false)); }
        finally
        {
            reader.Dispose();
            steps.Writer.TryComplete();
            if (ct.IsCancellationRequested) CleanUp();
        }
    }

    private ILeasedFile WriteSlice(byte[] pcm, int length)
    {
        var wav = files.Create("media", "audio/wav", "wav");
        try
        {
            using var target = options.OpenWrite?.Invoke(wav.FilePath) ?? new FileStream(wav.FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            target.Write(Wav16.Header(length, Rate));
            target.Write(pcm, 0, length);
            target.Flush();
            return wav;
        }
        catch { wav.Dispose(); throw; } // a partial file never survives
    }

    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 0x27 or 0x70;

    private void CleanUp()
    {
        while (steps.Reader.TryRead(out var step)) if (step is MediaStep.Slice s) s.Value.Dispose();
        MediaSlice[] left;
        lock (outstanding) left = [.. outstanding];
        foreach (var slice in left) slice.Dispose();
    }

    /// <summary>Cancels the decode, waits for it to stop and releases every slice not yet released (no temp file stays).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cts.Cancel();
        try { producer.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
        CleanUp();
        cts.Dispose();
    }
}
