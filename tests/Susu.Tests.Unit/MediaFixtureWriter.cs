using System.Runtime.InteropServices;

namespace Susu.Tests.Unit;

/// <summary>
/// Test-only encoder (F14.1): writes a 44.1 kHz stereo 16-bit sine as AAC in MP4/M4A or as MP3 through the Media Foundation sink
/// writer, so the real decoder can be tested on compressed media the machine itself can produce. Returns an error text instead of
/// throwing when this machine has no encoder or container writer (the caller then reports the case as not executed).
/// </summary>
internal static unsafe partial class MediaFixtureWriter
{
    private static readonly Guid Major = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F"), Sub = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5"),
        Audio = new("73647561-0000-0010-8000-00AA00389B71"), Pcm = new("00000001-0000-0010-8000-00AA00389B71"),
        Aac = new("00001610-0000-0010-8000-00AA00389B71"), Mp3 = new("00000055-0000-0010-8000-00AA00389B71"),
        Bits = new("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669"), Rate = new("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA"),
        Channels = new("37E48BF5-645E-4C5B-89DE-ADA9E29B696A"), Align = new("322DE230-9EEB-43BD-AB7A-FF412251541D"),
        Avg = new("1AAB75C8-CFEF-451C-AB95-AC034B8E1731"), Payload = new("BFBABE79-7434-4D1C-94F0-72A3B9E17188"),
        Profile = new("7632F0E6-9538-4D61-ACDA-EA29C8C14456");

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint mode);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("mfplat.dll")] private static partial int MFStartup(uint version, uint flags);
    [LibraryImport("mfplat.dll")] private static partial int MFShutdown();
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMediaType(out nint type);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateSample(out nint sample);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMemoryBuffer(uint max, out nint buffer);
    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MFCreateSinkWriterFromURL(string url, nint stream, nint attributes, out nint writer);

    /// <summary>Encodes <paramref name="seconds"/> of a 440 Hz sine; "mp4"/"m4a" make AAC, "mp3" makes MP3. Null on success.</summary>
    public static string? Write(string path, int seconds, double hz = 440)
    {
        string? error = null;
        var thread = new Thread(() => error = Run(path, seconds, hz));
        thread.Start(); thread.Join();
        return error;
    }

    private static string? Run(string path, int seconds, double hz)
    {
        bool mp3 = path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
        int init = CoInitializeEx(0, 0);
        int hr = MFStartup(0x00020070, 0);
        if (hr < 0) return $"MFStartup 0x{hr:X8}";
        nint writer = 0, output = 0, input = 0;
        try
        {
            hr = MFCreateSinkWriterFromURL(path, 0, 0, out writer);
            if (hr < 0) return $"no container writer 0x{hr:X8}";
            MFCreateMediaType(out output); MFCreateMediaType(out input);
            Set(output, Major, Audio); Set(output, Sub, mp3 ? Mp3 : Aac); Set(output, Bits, 16); Set(output, Rate, 44100); Set(output, Channels, 2);
            Set(output, Avg, mp3 ? 16000u : 12000u);
            if (mp3) Set(output, Align, 1);
            else { Set(output, Payload, 0); Set(output, Profile, 0x29); Set(output, Align, 1); }
            Set(input, Major, Audio); Set(input, Sub, Pcm); Set(input, Bits, 16); Set(input, Rate, 44100); Set(input, Channels, 2);
            Set(input, Align, 4); Set(input, Avg, 176400);
            uint stream;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(writer, 3))(writer, output, &stream);
            if (hr < 0) return $"no encoder for the output type 0x{hr:X8}";
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(writer, 4))(writer, stream, input, 0);
            if (hr < 0) return $"SetInputMediaType 0x{hr:X8}";
            hr = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(writer, 5))(writer);
            if (hr < 0) return $"BeginWriting 0x{hr:X8}";
            for (int s = 0; s < seconds; s++)
            {
                var pcm = new byte[44100 * 4];
                for (int i = 0; i < 44100; i++)
                {
                    short v = (short)(Math.Sin(2 * Math.PI * hz * (s * 44100 + i) / 44100) * 12000);
                    BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), v); BitConverter.TryWriteBytes(pcm.AsSpan(i * 4 + 2), v);
                }
                nint sample = 0, buffer = 0;
                try
                {
                    MFCreateSample(out sample); MFCreateMemoryBuffer((uint)pcm.Length, out buffer);
                    byte* data; uint max, cur;
                    ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &cur);
                    pcm.CopyTo(new Span<byte>(data, pcm.Length));
                    ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer);
                    ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, (uint)pcm.Length);
                    ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer);
                    ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, s * 10_000_000L);
                    ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, 10_000_000L);
                    hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(writer, 6))(writer, stream, sample);
                    if (hr < 0) return $"WriteSample 0x{hr:X8}";
                }
                finally { Release(buffer); Release(sample); }
            }
            hr = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(writer, 11))(writer);
            return hr < 0 ? $"Finalize 0x{hr:X8}" : null;
        }
        finally { Release(input); Release(output); Release(writer); MFShutdown(); if (init >= 0) CoUninitialize(); }
    }

    private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];
    private static void Release(nint obj) { if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj); }
    private static void Set(nint type, Guid key, Guid value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(type, 24))(type, &key, &value);
    private static void Set(nint type, Guid key, uint value) => ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(type, 21))(type, &key, value);
}
