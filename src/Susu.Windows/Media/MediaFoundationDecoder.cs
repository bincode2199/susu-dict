using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Susu.Abstractions;
using Susu.Windows.Audio;

namespace Susu.Windows.Media;

/// <summary>
/// The Media Foundation layer of video transcription (F14.1, ARCHITECTURE 7 <c>IMediaDecoder</c>; explicit vtable calls like the
/// other audio code). A source reader opens the file by host path, every audio stream is listed (codec, rate, channels) and tested
/// for a decoder by asking for 16 kHz mono 16-bit PCM; the first stream that has one is decoded, video streams are deselected.
/// All COM objects of one file live on one dedicated MTA thread, released when the reader is disposed.
///
/// <para>Error mapping: file missing is media.notFound; a container MF does not recognize (unsupported or too damaged to tell) is
/// media.unsupportedEncoding; other open failures with a recognized container are media.corrupt; no audio stream is
/// media.noAudio; audio streams all without decoder (e.g. a Windows N edition without the codec pack) is
/// media.unsupportedEncoding naming the codecs; a failure while reading samples is media.corrupt.</para>
/// </summary>
public sealed partial class MediaFoundationDecoder : IMediaDecoder
{
    public const int TargetRate = 16000;
    private static readonly Guid MajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid Subtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MediaAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MediaVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid BitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");
    private static readonly Guid SamplesPerSecond = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA");
    private static readonly Guid NumChannels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A");
    private static readonly Guid BlockAlignment = new("322DE230-9EEB-43BD-AB7A-FF412251541D");
    private static readonly Guid AvgBytesPerSecond = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731");
    private static readonly Guid PdDuration = new("6C990D33-BB8E-477A-8598-0D5D96FCD88A");
    private const uint AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    private const uint ReaderError = 0x1, ReaderEnd = 0x2;
    private const int InvalidStreamIndex = unchecked((int)0xC00D36B3), UnsupportedBytestream = unchecked((int)0xC00D36C4),
        FileNotFound = unchecked((int)0x80070002), PathNotFound = unchecked((int)0x80070003);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMediaType(out nint mediaType);

    public async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var reader = await OpenCoreAsync(path, cancellationToken);
        try { return reader.Probe; }
        finally { reader.Dispose(); }
    }

    public async Task<IMediaAudioReader> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var reader = await OpenCoreAsync(path, cancellationToken);
        var probe = reader.Probe;
        string? error = probe.AudioStreams.Count == 0 ? MediaErrors.NoAudio : probe.Selected is null ? MediaErrors.UnsupportedEncoding : null;
        if (error is null) return reader;
        reader.Dispose();
        throw new MediaDecodeException(error, error == MediaErrors.NoAudio
            ? "the file has no audio stream"
            : "no audio stream can be decoded here: " + string.Join(", ", probe.AudioStreams.Select(s => s.Codec)));
    }

    private static async Task<Reader> OpenCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new MediaDecodeException(MediaErrors.NotFound, "the file does not exist");
        var worker = new Worker();
        try
        {
            var reader = await worker.Invoke(() => Reader.Open(path, worker));
            return reader;
        }
        catch { worker.Dispose(); throw; }
    }

    internal static string CodecName(Guid subtype)
    {
        byte[] b = subtype.ToByteArray();
        if (!b.AsSpan(4).SequenceEqual(FormatPcm.ToByteArray().AsSpan(4))) return subtype.ToString("D");
        return BitConverter.ToUInt32(b, 0) switch
        {
            0x1 => "pcm", 0x3 => "pcm-float", 0x55 => "mp3", 0xFF or 0x1610 => "aac", 0x161 or 0x162 or 0x163 => "wma",
            0x2000 => "ac3", 0x704F => "opus", 0x566F => "vorbis", 0xF1AC => "flac", 0x11 or 0x2 => "adpcm",
            uint tag => $"0x{tag:X}",
        };
    }

    /// <summary>One thread that owns the COM objects of one opened file.</summary>
    private sealed class Worker : IDisposable
    {
        private readonly BlockingCollection<Action> queue = [];
        private readonly Thread thread;
        private int disposed;

        public Worker()
        {
            thread = new Thread(() =>
            {
                int init = NativeMethods.CoInitializeEx(0, 0);
                try { foreach (var work in queue.GetConsumingEnumerable()) work(); }
                finally { if (init >= 0) NativeMethods.CoUninitialize(); }
            }) { IsBackground = true, Name = "susu-media-decode" };
            thread.Start();
        }

        public Task<T> Invoke<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                queue.Add(() =>
                {
                    try { done.TrySetResult(work()); }
                    catch (Exception e) { done.TrySetException(e); }
                });
            }
            catch (InvalidOperationException) { done.TrySetException(new ObjectDisposedException(nameof(Worker))); }
            return done.Task;
        }

        /// <summary>Runs <paramref name="cleanup"/> on the worker thread, then stops it.</summary>
        public void Dispose(Action cleanup)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { queue.Add(cleanup); queue.CompleteAdding(); } catch (InvalidOperationException) { }
            if (thread != Thread.CurrentThread) thread.Join(5000);
        }

        public void Dispose() => Dispose(() => { });
    }

    private sealed unsafe class Reader : IMediaAudioReader
    {
        private readonly Worker worker;
        private nint reader;
        private readonly uint stream;
        private bool started;
        private int disposed;

        private Reader(Worker worker, nint reader, uint stream, MediaProbe probe, bool started)
        {
            this.worker = worker; this.reader = reader; this.stream = stream; Probe = probe; this.started = started;
        }

        public MediaProbe Probe { get; }

        public static Reader Open(string path, Worker worker)
        {
            int startup = NativeMethods.MFStartup(0x00020070, 1);
            if (startup < 0) throw new MediaDecodeException(MediaErrors.OpenFailed, $"MFStartup 0x{startup:X8}");
            nint source = 0;
            try
            {
                int hr = MFCreateSourceReaderFromURL(path, 0, out source);
                if (hr < 0)
                    throw new MediaDecodeException(hr switch
                    {
                        FileNotFound or PathNotFound => MediaErrors.NotFound,
                        UnsupportedBytestream => MediaErrors.UnsupportedEncoding,
                        _ => MediaErrors.Corrupt,
                    }, $"cannot open media 0x{hr:X8}");
                var (probe, selected) = Inspect(source);
                var reader = new Reader(worker, source, selected, probe, true);
                source = 0; // owned by the reader now
                return reader;
            }
            finally
            {
                if (source != 0) { Com.Release(source); NativeMethods.MFShutdown(); }
            }
        }

        private static (MediaProbe, uint) Inspect(nint source)
        {
            var streams = new List<MediaAudioStream>();
            bool video = false;
            var setSelection = (delegate* unmanaged[Stdcall]<nint, uint, int, int>)Com.Slot(source, 4);
            setSelection(source, AllStreams, 0);
            for (uint i = 0; i < 64; i++)
            {
                nint type = 0;
                int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Com.Slot(source, 5))(source, i, 0, &type);
                if (hr == InvalidStreamIndex) break;
                if (hr < 0 || type == 0) continue;
                try
                {
                    Guid major;
                    if (GetGuid(type, MajorType, &major) < 0) continue;
                    if (major == MediaVideo) { video = true; continue; }
                    if (major != MediaAudio) continue;
                    Guid sub = default;
                    GetGuid(type, Subtype, &sub);
                    uint rate = 0, channels = 0;
                    GetUInt32(type, SamplesPerSecond, &rate);
                    GetUInt32(type, NumChannels, &channels);
                    streams.Add(new MediaAudioStream((int)i, CodecName(sub), (int)rate, (int)channels, RequestPcm(source, i) >= 0));
                }
                finally { Com.Release(type); }
            }
            long ticks = Duration(source);
            var probe = new MediaProbe(TimeSpan.FromTicks(ticks), video, streams);
            uint selected = probe.Selected is { } s ? (uint)s.Index : 0;
            if (probe.Selected is not null) { setSelection(source, selected, 1); RequestPcm(source, selected); }
            return (probe, selected);
        }

        /// <summary>Asks the stream for 16 kHz mono 16-bit PCM; failure means no decoder (or no converter) for it.</summary>
        private static int RequestPcm(nint source, uint index)
        {
            int hr = MFCreateMediaType(out nint type);
            if (hr < 0) return hr;
            try
            {
                Guid major = MajorType, audio = MediaAudio, subtype = Subtype, pcm = FormatPcm, bits = BitsPerSample,
                    rate = SamplesPerSecond, channels = NumChannels, align = BlockAlignment, avg = AvgBytesPerSecond;
                var setGuid = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Com.Slot(type, 24);
                var setUInt = (delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Com.Slot(type, 21);
                setGuid(type, &major, &audio); setGuid(type, &subtype, &pcm);
                setUInt(type, &bits, 16); setUInt(type, &rate, TargetRate); setUInt(type, &channels, 1);
                setUInt(type, &align, 2); setUInt(type, &avg, TargetRate * 2);
                return ((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)Com.Slot(source, 7))(source, index, null, type);
            }
            finally { Com.Release(type); }
        }

        private static long Duration(nint source)
        {
            Span<byte> variant = stackalloc byte[24];
            variant.Clear();
            Guid key = PdDuration;
            fixed (byte* v = variant)
            {
                int hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, byte*, int>)Com.Slot(source, 12))(source, MediaSource, &key, v);
                return hr >= 0 && BitConverter.ToUInt16(variant) == 21 ? (long)BitConverter.ToUInt64(variant[8..]) : 0; // VT_UI8
            }
        }

        private static int GetGuid(nint attributes, Guid key, Guid* value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Com.Slot(attributes, 10))(attributes, &key, value);
        private static int GetUInt32(nint attributes, Guid key, uint* value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Com.Slot(attributes, 7))(attributes, &key, value);

        public Task<MediaPcmBlock?> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return worker.Invoke(() => ReadOne(cancellationToken));
        }

        private MediaPcmBlock? ReadOne(CancellationToken cancellationToken)
        {
            if (reader == 0) return null;
            var readSample = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Com.Slot(reader, 9);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint streamIndex, flags;
                long timestamp;
                nint sample = 0;
                int hr = readSample(reader, stream, 0, &streamIndex, &flags, &timestamp, &sample);
                if (hr < 0) throw new MediaDecodeException(MediaErrors.Corrupt, $"decode failed 0x{hr:X8}");
                try
                {
                    if ((flags & ReaderError) != 0) throw new MediaDecodeException(MediaErrors.Corrupt, "decode stream error");
                    if (sample != 0)
                    {
                        byte[]? pcm = Copy(sample);
                        if (pcm is { Length: > 0 }) return new MediaPcmBlock(pcm, TimeSpan.FromTicks(timestamp));
                    }
                    if ((flags & ReaderEnd) != 0) return null;
                }
                finally { Com.Release(sample); }
            }
        }

        private static byte[]? Copy(nint sample)
        {
            nint buffer = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.Slot(sample, 41))(sample, &buffer);
            if (hr < 0) throw new MediaDecodeException(MediaErrors.Corrupt, $"decode failed 0x{hr:X8}");
            try
            {
                byte* data; uint max, length;
                hr = ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Com.Slot(buffer, 3))(buffer, &data, &max, &length);
                if (hr < 0) throw new MediaDecodeException(MediaErrors.Corrupt, $"decode failed 0x{hr:X8}");
                try { return new ReadOnlySpan<byte>(data, (int)length).ToArray(); }
                finally { ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(buffer, 4))(buffer); }
            }
            finally { Com.Release(buffer); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            worker.Dispose(() =>
            {
                Com.Release(reader); reader = 0;
                if (started) { NativeMethods.MFShutdown(); started = false; }
            });
        }
    }
}
