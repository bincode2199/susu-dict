using System.Runtime.InteropServices;
using System.Threading.Channels;
using Susu.Abstractions;

namespace Susu.Windows.Audio;

/// <summary>
/// The WASAPI capture layer of the microphone recorder (F12.1, PLAN 6.3, ARCHITECTURE 1/7: explicit vtable calls, no RCW).
/// The default console capture endpoint is opened in shared mode as 16 kHz mono 16-bit PCM (Windows converts through
/// <c>AUTOCONVERTPCM</c>, so no resampling code lives here). Every COM object lives on one dedicated MTA thread that polls the
/// capture client every 10 ms and hands blocks to the reader through a channel; the thread releases everything when the stream
/// is disposed, which is how the device is released on stop, cancel, close and exit.
///
/// <para>Errors are classified (<see cref="MicFailure"/>): E_ACCESSDENIED from activation or initialization is Denied; no default
/// capture endpoint is NoDevice at open and DeviceRemoved mid-stream; an invalidated stream is DeviceRemoved, or DefaultChanged
/// when the default capture endpoint now has another id. The stream never reopens another device by itself.</para>
/// </summary>
public sealed unsafe class WasapiMicrophone : IMicrophoneDevices
{
    public const int CaptureRate = 16000;
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private const uint AutoConvertPcm = 0x80000000, SrcDefaultQuality = 0x08000000;
    private const uint BufferSilent = 0x2;
    private const int ENotFound = unchecked((int)0x80070490), EAccessDenied = unchecked((int)0x80070005),
        DeviceInvalidated = unchecked((int)0x88890004), ServiceNotRunning = unchecked((int)0x88890010),
        EndpointCreateFailed = unchecked((int)0x8889000F), ResourcesInvalidated = unchecked((int)0x88890026);

    public bool HasDevice()
    {
        return Com.RunMta(() =>
        {
            nint enumerator = 0, device = 0;
            try
            {
                enumerator = Com.Create(ClsidMMDeviceEnumerator, IidMMDeviceEnumerator, Com.ClsctxAll);
                return DefaultCapture(enumerator, out device) >= 0 && device != 0;
            }
            catch (COMException) { return false; }
            finally { Com.Release(device); Com.Release(enumerator); }
        }, "susu-mic-probe").GetAwaiter().GetResult();
    }

    public IMicrophoneStream Open()
    {
        var stream = new Stream();
        stream.Start();
        return stream;
    }

    public static MicFailure Classify(int hr) => hr switch
    {
        EAccessDenied => MicFailure.Denied,
        ENotFound or ServiceNotRunning or EndpointCreateFailed => MicFailure.NoDevice,
        DeviceInvalidated or ResourcesInvalidated => MicFailure.DeviceRemoved,
        _ => MicFailure.Failed,
    };

    private static int DefaultCapture(nint enumerator, out nint device)
    {
        nint d = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Com.Slot(enumerator, 4))(enumerator, 1, 0, &d); // eCapture, eConsole
        device = d;
        return hr;
    }

    private static string? DeviceId(nint device)
    {
        nint id = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.Slot(device, 5))(device, &id);
        return hr < 0 ? null : Com.TakeString(id);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new MicrophoneException(Classify(hr), $"{what} 0x{hr:X8}");
    }

    private sealed class Stream : IMicrophoneStream
    {
        private readonly Channel<byte[]> blocks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly ManualResetEventSlim stop = new(false);
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Thread? thread;

        public int SampleRate => CaptureRate;

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "susu-mic-capture" };
            thread.Start();
            try { ready.Task.GetAwaiter().GetResult(); }
            catch { thread.Join(2000); throw; }
        }

        public Task<byte[]?> ReadAsync(CancellationToken cancellationToken) => MicChannelReader.Read(blocks.Reader, cancellationToken);

        public void Dispose()
        {
            stop.Set();
            if (thread is not null && thread != Thread.CurrentThread) thread.Join(3000);
        }

        private void Run()
        {
            int init = NativeMethods.CoInitializeEx(0, 0);
            nint enumerator = 0, device = 0, client = 0, capture = 0;
            bool started = false;
            try
            {
                string? openedId;
                try
                {
                    enumerator = Com.Create(ClsidMMDeviceEnumerator, IidMMDeviceEnumerator, Com.ClsctxAll);
                    int hr = DefaultCapture(enumerator, out device);
                    if (hr < 0 || device == 0) throw new MicrophoneException(MicFailure.NoDevice, $"no default capture device 0x{hr:X8}");
                    openedId = DeviceId(device);
                    Guid iid = IidAudioClient;
                    nint* pc = &client;
                        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Com.Slot(device, 3))(device, &iid, Com.ClsctxAll, 0, pc), "IMMDevice.Activate");
                    byte* wfx = stackalloc byte[18];
                    new Span<byte>(wfx, 18).Clear();
                    *(ushort*)wfx = 1; *(ushort*)(wfx + 2) = 1; *(int*)(wfx + 4) = CaptureRate; *(int*)(wfx + 8) = CaptureRate * 2;
                    *(ushort*)(wfx + 12) = 2; *(ushort*)(wfx + 14) = 16;
                    Check(((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, byte*, Guid*, int>)Com.Slot(client, 3))(client, 0, AutoConvertPcm | SrcDefaultQuality, 2_000_000, 0, wfx, null), "IAudioClient.Initialize");
                    Guid captureId = IidAudioCaptureClient;
                    nint* pcap = &capture;
                        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Com.Slot(client, 14))(client, &captureId, pcap), "IAudioClient.GetService");
                    Check(((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(client, 10))(client), "IAudioClient.Start");
                    started = true;
                }
                catch (COMException e) { throw new MicrophoneException(Classify(e.HResult), $"audio capture 0x{e.HResult:X8}"); }
                ready.TrySetResult();

                var nextPacket = (delegate* unmanaged[Stdcall]<nint, uint*, int>)Com.Slot(capture, 5);
                var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, ulong*, ulong*, int>)Com.Slot(capture, 3);
                var releaseBuffer = (delegate* unmanaged[Stdcall]<nint, uint, int>)Com.Slot(capture, 4);
                long nextDefaultCheck = Environment.TickCount64 + 250;
                try
                {
                    while (!stop.Wait(10))
                    {
                        while (true)
                        {
                            uint frames;
                            Check(nextPacket(capture, &frames), "IAudioCaptureClient.GetNextPacketSize");
                            if (frames == 0) break;
                            byte* data; uint count, flags; ulong devicePosition, qpc;
                            Check(getBuffer(capture, &data, &count, &flags, &devicePosition, &qpc), "IAudioCaptureClient.GetBuffer");
                            var block = new byte[(int)count * 2];
                            if ((flags & BufferSilent) == 0) new ReadOnlySpan<byte>(data, block.Length).CopyTo(block);
                            Check(releaseBuffer(capture, count), "IAudioCaptureClient.ReleaseBuffer");
                            blocks.Writer.TryWrite(block);
                        }
                        if (Environment.TickCount64 >= nextDefaultCheck)
                        {
                            nextDefaultCheck = Environment.TickCount64 + 250;
                            nint current = 0;
                            int hr = DefaultCapture(enumerator, out current);
                            try
                            {
                                if (hr < 0 || current == 0) throw new MicrophoneException(MicFailure.DeviceRemoved, "default capture device is gone");
                                if (DeviceId(current) != openedId) throw new MicrophoneException(MicFailure.DefaultChanged, "default capture device changed");
                            }
                            finally { Com.Release(current); }
                        }
                    }
                    blocks.Writer.TryComplete();
                }
                catch (MicrophoneException e)
                {
                    var failure = e;
                    if (e.Failure == MicFailure.DeviceRemoved)
                    {
                        // An invalidated stream: say DefaultChanged when another device is now the default.
                        nint current = 0;
                        try
                        {
                            if (DefaultCapture(enumerator, out current) >= 0 && current != 0 && DeviceId(current) != openedId)
                                failure = new MicrophoneException(MicFailure.DefaultChanged, "default capture device changed");
                        }
                        finally { Com.Release(current); }
                    }
                    blocks.Writer.TryComplete(failure);
                }
            }
            catch (MicrophoneException e) { ready.TrySetException(e); blocks.Writer.TryComplete(e); }
            catch (Exception e) { var f = new MicrophoneException(MicFailure.Failed, e.GetType().Name); ready.TrySetException(f); blocks.Writer.TryComplete(f); }
            finally
            {
                if (started) ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(client, 11))(client);
                Com.Release(capture); Com.Release(client); Com.Release(device); Com.Release(enumerator);
                if (init >= 0) NativeMethods.CoUninitialize();
            }
        }
    }
}

internal static class MicChannelReader
{
    public static async Task<byte[]?> Read(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        try { return await reader.ReadAsync(cancellationToken); }
        catch (ChannelClosedException e) { if (e.InnerException is MicrophoneException m) throw m; return null; }
    }
}
