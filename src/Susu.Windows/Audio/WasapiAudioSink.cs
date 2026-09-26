using System.Runtime.InteropServices;
using Susu.Abstractions;

namespace Susu.Windows.Audio;

/// <summary>
/// The audio output (F10.1, PLAN 9: Media Foundation decode, WASAPI output; ARCHITECTURE 7 worker threads). A clip is
/// decoded with <c>IMFSourceReader</c> to 16-bit PCM at its own rate/channels (MP3, WAV, AAC/M4A, FLAC and whatever else
/// MF can read), then rendered to the default console endpoint in shared mode with Windows' built-in PCM conversion
/// (<c>AUTOCONVERTPCM</c>), so no resampling code lives here. Everything runs on its own MTA thread; the caller only
/// awaits. Cancellation stops the stream within one poll (about 10 ms).
///
/// Errors are classified (<see cref="AudioFailure"/>): no endpoint or no audio service is <c>NoDevice</c>; an endpoint
/// that disappears mid-stream is <c>DeviceLost</c>; a file MF cannot decode is <c>Unsupported</c>.
/// </summary>
public sealed unsafe partial class WasapiAudioSink : IAudioSink
{
    /// <summary>Decoded PCM is held in memory; a TTS clip is seconds long, this caps a pathological file (about 6 min of 48 kHz stereo).</summary>
    public const int MaxPcmBytes = 64 << 20;

    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    private static readonly Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    private static readonly Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMtAudioBitsPerSample = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669");

    private const uint FirstAudioStream = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE;
    private const uint ReaderEndOfStream = 0x2, ReaderError = 0x1;
    private const uint AutoConvertPcm = 0x80000000, SrcDefaultQuality = 0x08000000;
    private const int ENotFound = unchecked((int)0x80070490), DeviceInvalidated = unchecked((int)0x88890004), ServiceNotRunning = unchecked((int)0x88890010),
        UnsupportedFormat = unchecked((int)0x88890008), EndpointCreateFailed = unchecked((int)0x8889000F), ResourcesInvalidated = unchecked((int)0x88890026);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMediaType(out nint mediaType);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateWaveFormatExFromMFMediaType(nint mediaType, out nint format, out uint size, uint flags);

    public AudioDeviceStatus Probe()
    {
        try { return Com.RunMta(ProbeOnThread, "susu-audio-probe").GetAwaiter().GetResult(); }
        catch (Exception error) { return new AudioDeviceStatus(false, AudioFailure.Failed, error.GetType().Name); }
    }

    private static AudioDeviceStatus ProbeOnThread()
    {
        nint enumerator = 0, device = 0, client = 0;
        try
        {
            enumerator = Com.Create(ClsidMMDeviceEnumerator, IidMMDeviceEnumerator, Com.ClsctxAll);
            device = DefaultEndpoint(enumerator);
            client = Activate(device);
            return new AudioDeviceStatus(true);
        }
        catch (AudioPlaybackException error) { return new AudioDeviceStatus(false, error.Failure, error.Message); }
        catch (COMException error) { return new AudioDeviceStatus(false, Classify(error.HResult), $"0x{error.HResult:X8}"); }
        finally { Com.Release(client); Com.Release(device); Com.Release(enumerator); }
    }

    public Task PlayAsync(IAudioClip clip, CancellationToken cancellationToken)
    {
        string path = clip.FilePath;
        return Com.RunMta(() => { Play(path, cancellationToken); return true; }, "susu-audio-play");
    }

    private static void Play(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (pcm, format) = Decode(path, cancellationToken);
        nint enumerator = 0, device = 0, client = 0, render = 0;
        try
        {
            try
            {
                enumerator = Com.Create(ClsidMMDeviceEnumerator, IidMMDeviceEnumerator, Com.ClsctxAll);
                device = DefaultEndpoint(enumerator);
                client = Activate(device);
            }
            catch (COMException error) { throw new AudioPlaybackException(Classify(error.HResult), $"audio endpoint 0x{error.HResult:X8}"); }
            fixed (byte* wfx = format)
                Device(((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, byte*, Guid*, int>)Com.Slot(client, 3))(client, 0, AutoConvertPcm | SrcDefaultQuality, 2_000_000, 0, wfx, null), "IAudioClient.Initialize");
            uint bufferFrames;
            Device(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Com.Slot(client, 4))(client, &bufferFrames), "IAudioClient.GetBufferSize");
            Guid renderId = IidAudioRenderClient;
            Device(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.Slot(client, 14))(client, &renderId, &render), "IAudioClient.GetService");
            int blockAlign = BitConverter.ToUInt16(format, 12);
            int sampleRate = BitConverter.ToInt32(format, 4);
            var padding = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Com.Slot(client, 6);
            var getBuffer = (delegate* unmanaged[Stdcall]<nint, uint, byte**, int>)Com.Slot(render, 3);
            var releaseBuffer = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Com.Slot(render, 4);
            bool started = false;
            int offset = 0;
            try
            {
                // A stalled endpoint (padding never drains) must not hold the player forever.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(pcm.Length / (double)Math.Max(1, sampleRate * blockAlign) + 5);
                while (true)
                {
                    uint queued;
                    Device(padding(client, &queued), "IAudioClient.GetCurrentPadding");
                    int left = (pcm.Length - offset) / blockAlign;
                    if (left == 0 && queued == 0) break; // everything written and played out
                    if (DateTime.UtcNow > deadline) throw new AudioPlaybackException(AudioFailure.DeviceLost, "audio output stopped consuming samples");
                    int frames = Math.Min((int)(bufferFrames - Math.Min(queued, bufferFrames)), left);
                    if (frames > 0)
                    {
                        byte* target;
                        Device(getBuffer(render, (uint)frames, &target), "IAudioRenderClient.GetBuffer");
                        pcm.AsSpan(offset, frames * blockAlign).CopyTo(new Span<byte>(target, frames * blockAlign));
                        Device(releaseBuffer(render, (uint)frames, 0), "IAudioRenderClient.ReleaseBuffer");
                        offset += frames * blockAlign;
                    }
                    if (!started) { Device(((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(client, 10))(client), "IAudioClient.Start"); started = true; }
                    if (cancellationToken.WaitHandle.WaitOne(10)) cancellationToken.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                if (started) ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(client, 11))(client);
            }
        }
        finally
        {
            Com.Release(render); Com.Release(client); Com.Release(device); Com.Release(enumerator);
        }
    }

    private static nint DefaultEndpoint(nint enumerator)
    {
        nint device;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Com.Slot(enumerator, 4))(enumerator, 0, 0, &device); // eRender, eConsole
        if (hr < 0 || device == 0) throw new AudioPlaybackException(Classify(hr == 0 ? ENotFound : hr), $"no default audio output (0x{hr:X8})");
        return device;
    }

    private static nint Activate(nint device)
    {
        nint client;
        Guid iid = IidAudioClient;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Com.Slot(device, 3))(device, &iid, Com.ClsctxAll, 0, &client);
        if (hr < 0) throw new AudioPlaybackException(Classify(hr), $"audio endpoint activation 0x{hr:X8}");
        return client;
    }

    public static AudioFailure Classify(int hr) => hr switch
    {
        ENotFound or ServiceNotRunning or EndpointCreateFailed => AudioFailure.NoDevice,
        DeviceInvalidated or ResourcesInvalidated => AudioFailure.DeviceLost, // unplugged/disabled/default changed; or the stream was invalidated
        UnsupportedFormat => AudioFailure.Unsupported,
        _ => AudioFailure.Failed,
    };

    private static void Device(int hr, string what)
    {
        if (hr < 0) throw new AudioPlaybackException(Classify(hr), $"{what} 0x{hr:X8}");
    }

    /// <summary>Decodes the whole clip to 16-bit PCM; returns the bytes and the WAVEFORMATEX describing them.</summary>
    public static (byte[] Pcm, byte[] Format) Decode(string path, CancellationToken cancellationToken = default)
    {
        int startup = NativeMethods.MFStartup(0x00020070, 1); // MF_VERSION, MFSTARTUP_LITE
        if (startup < 0) throw new AudioPlaybackException(AudioFailure.Failed, $"MFStartup 0x{startup:X8}");
        nint reader = 0, requested = 0, actual = 0;
        try
        {
            int hr = MFCreateSourceReaderFromURL(path, 0, out reader);
            if (hr < 0) throw new AudioPlaybackException(AudioFailure.Unsupported, $"cannot open audio 0x{hr:X8}");
            Decoder(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Com.Slot(reader, 4))(reader, AllStreams, 0), "SetStreamSelection");
            Decoder(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Com.Slot(reader, 4))(reader, FirstAudioStream, 1), "SetStreamSelection(audio)");
            Decoder(MFCreateMediaType(out requested), "MFCreateMediaType");
            Guid major = MfMtMajorType, audio = MfMediaTypeAudio, subtype = MfMtSubtype, pcmFormat = MfAudioFormatPcm, bits = MfMtAudioBitsPerSample;
            Decoder(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Com.Slot(requested, 24))(requested, &major, &audio), "SetGUID(major)");
            Decoder(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Com.Slot(requested, 24))(requested, &subtype, &pcmFormat), "SetGUID(subtype)");
            Decoder(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Com.Slot(requested, 21))(requested, &bits, 16), "SetUINT32(bits)");
            Decoder(((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)Com.Slot(reader, 7))(reader, FirstAudioStream, null, requested), "SetCurrentMediaType");
            Decoder(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Com.Slot(reader, 6))(reader, FirstAudioStream, &actual), "GetCurrentMediaType");
            Decoder(MFCreateWaveFormatExFromMFMediaType(actual, out nint wfx, out uint size, 0), "MFCreateWaveFormatExFromMFMediaType");
            byte[] format;
            try { format = new ReadOnlySpan<byte>((void*)wfx, (int)size).ToArray(); }
            finally { Com.CoTaskMemFree(wfx); }
            if (format.Length < 18 || BitConverter.ToUInt16(format, 12) == 0) throw new AudioPlaybackException(AudioFailure.Unsupported, "decoder returned no PCM format");

            using var pcm = new MemoryStream();
            var readSample = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Com.Slot(reader, 9);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint streamIndex, flags;
                long timestamp;
                nint sample = 0;
                Decoder(readSample(reader, FirstAudioStream, 0, &streamIndex, &flags, &timestamp, &sample), "ReadSample");
                try
                {
                    if ((flags & ReaderError) != 0) throw new AudioPlaybackException(AudioFailure.Unsupported, "decoder stream error");
                    if (sample != 0)
                    {
                        nint buffer = 0;
                        Decoder(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.Slot(sample, 41))(sample, &buffer), "ConvertToContiguousBuffer");
                        try
                        {
                            byte* data; uint max, length;
                            Decoder(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Com.Slot(buffer, 3))(buffer, &data, &max, &length), "IMFMediaBuffer.Lock");
                            try
                            {
                                if (pcm.Length + length > MaxPcmBytes) throw new AudioPlaybackException(AudioFailure.Unsupported, "clip too long");
                                pcm.Write(new ReadOnlySpan<byte>(data, (int)length));
                            }
                            finally { ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(buffer, 4))(buffer); }
                        }
                        finally { Com.Release(buffer); }
                    }
                }
                finally { Com.Release(sample); }
                if ((flags & ReaderEndOfStream) != 0) break;
            }
            if (pcm.Length == 0) throw new AudioPlaybackException(AudioFailure.Unsupported, "clip has no audio");
            return (pcm.ToArray(), format);
        }
        finally
        {
            Com.Release(actual); Com.Release(requested); Com.Release(reader);
            NativeMethods.MFShutdown();
        }
    }

    private static void Decoder(int hr, string what)
    {
        if (hr < 0) throw new AudioPlaybackException(AudioFailure.Unsupported, $"{what} 0x{hr:X8}");
    }
}
