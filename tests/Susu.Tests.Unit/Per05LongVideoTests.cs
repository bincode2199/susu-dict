using System.Buffers.Binary;
using System.Diagnostics;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Susu.Windows.Media;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F14.4 PER05 video part, SYNTHETIC: a 42-minute input runs through the real <see cref="VideoJobs"/>, <see cref="MediaSlicer"/> (5-minute
/// slices, at most two ahead) and the real slice writer, with a fake ASR (one cue per slice) and a fake translator. One run uses a fake
/// reader (exact, no codec); the other a real 42-minute 16 kHz mono PCM WAV through Media Foundation. It measures peak private bytes,
/// peak working set and wall time of this test process and checks that no lease, slice file or token entry is left. No vendor request is made,
/// so it says nothing about network or ASR time. Serialized with the other media tests: they share the process-wide measurements. When
/// SUSU_PER05_OUT names a file, the numbers are appended there (they are recorded in docs/evidence/F14/F14.md).
/// </summary>
[Collection(MediaDecodeCollection.Name)]
public sealed class Per05LongVideoTests : IDisposable
{
    private const int Rate = 16000, Seconds = 42 * 60;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempRoot root = new();
    private readonly FileLeases leases;

    public Per05LongVideoTests() => leases = new FileLeases(root.Paths.Cache);
    public void Dispose() { leases.Dispose(); root.Dispose(); }

    private sealed class SegmentAsr : IAsrProvider
    {
        public int Calls;
        public string InstanceId => "asr";
        public string Model => "fake";
        public bool Timecodes => true;
        public AsrLimits Limits { get; } = new([AsrFormat.Wav16kMono], 24_000_000, 25_000_000, 300, AsrUpload.Multipart, 2048);
        public Task<AsrOutcome> TranscribeAsync(AsrCall call, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref Calls) - 1;
            return Task.FromResult<AsrOutcome>(new AsrOutcome.Transcribed("segments", "x", [new AsrSegment(1, 3, $"slice {n}")]));
        }
    }

    private sealed record Measure(double Seconds, long PeakPrivateMb, long PeakWorkingSetMb, long BaselinePrivateMb, long EndPrivateMb, int Slices, int Cues);

    private async Task<Measure> RunAsync(IMediaDecoder decoder, string file, string label)
    {
        var tokens = new MediaTokens();
        string token = tokens.Issue(file)!;
        var asr = new SegmentAsr();
        var jobs = new VideoJobs(tokens, decoder, new LeasedFiles(leases), () => asr,
            () => new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits)) { Confirmation = new VideoJobTests.Confirm() };
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        long baseline = self.PrivateMemorySize64, peakPrivate = baseline;
        using var sampler = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            using var p = Process.GetCurrentProcess();
            while (!sampler.IsCancellationRequested)
            {
                p.Refresh();
                long now = p.PrivateMemorySize64;
                long seen = Interlocked.Read(ref peakPrivate);
                if (now > seen) Interlocked.CompareExchange(ref peakPrivate, now, seen);
                try { await Task.Delay(25, sampler.Token); } catch (OperationCanceledException) { }
            }
        });
        var clock = Stopwatch.StartNew();
        var start = jobs.Start(token, "en", "zh");
        Assert.NotNull(start.Job);
        await start.Job!.Completion.WaitAsync(TimeSpan.FromMinutes(4), Ct);
        clock.Stop();
        sampler.Cancel();
        await watch;
        self.Refresh();
        long peakWs = self.PeakWorkingSet64;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        self.Refresh();
        var state = start.Job.State;
        Assert.Equal(VideoJobPhase.Done, state.Phase);
        Assert.Equal(0, leases.ActiveCount);
        Assert.Empty(Directory.Exists(root.Paths.Cache) ? Directory.EnumerateFiles(root.Paths.Cache, "*", SearchOption.AllDirectories) : []);
        var m = new Measure(clock.Elapsed.TotalSeconds, peakPrivate / (1 << 20), peakWs / (1 << 20), baseline / (1 << 20), self.PrivateMemorySize64 / (1 << 20), state.SlicesDone, state.Cues);
        string line = $"PER05-synthetic {label}: media 42:00, {m.Slices} slices, {m.Cues} cues, asr calls {asr.Calls}, {m.Seconds:F1} s, peak private {m.PeakPrivateMb} MB (baseline {m.BaselinePrivateMb}, end {m.EndPrivateMb}), peak working set {m.PeakWorkingSetMb} MB";
        TestContext.Current.SendDiagnosticMessage(line);
        if (Environment.GetEnvironmentVariable("SUSU_PER05_OUT") is { Length: > 0 } outFile) File.AppendAllText(outFile, line + Environment.NewLine);
        return m;
    }

    [Fact] // fake reader: 42 minutes of audio blocks, real slicing and temp slices; memory stays bounded by the two-slice window
    public async Task Forty_two_minutes_fake_reader_is_bounded_and_leaves_nothing()
    {
        var probe = new MediaProbe(TimeSpan.FromSeconds(Seconds), true, [new MediaAudioStream(1, "aac", 44100, 2, true)]);
        var decoder = new VideoJobTests.FakeDecoder(() => new VideoJobTests.FakeReader(probe, VideoJobTests.Blocks(Seconds, _ => true).GetEnumerator()));
        string file = Path.Combine(root.Root, "long.mp4"); File.WriteAllText(file, "x");
        var m = await RunAsync(decoder, file, "fake reader");
        Assert.Equal(9, m.Slices);
        Assert.Equal(9, m.Cues);
        // Two 5-minute slices are 2 x 9.6 MB on disk, not in memory; the whole file (80 MB of PCM) is never held.
        Assert.True(m.PeakPrivateMb - m.BaselinePrivateMb < 200, $"peak private grew {m.PeakPrivateMb - m.BaselinePrivateMb} MB");
    }

    [Fact] // real Media Foundation over a real 42-minute 16 kHz mono WAV (80.6 MB) written by the test
    public async Task Forty_two_minutes_real_wav_through_media_foundation()
    {
        string file = Path.Combine(root.Root, "long.wav");
        WriteWav(file, Seconds);
        Assert.True(new FileInfo(file).Length > 80_000_000);
        var m = await RunAsync(new MediaFoundationDecoder(), file, "real MF WAV");
        Assert.Equal(9, m.Slices);
        Assert.Equal(9, m.Cues);
        Assert.True(m.PeakPrivateMb - m.BaselinePrivateMb < 200, $"peak private grew {m.PeakPrivateMb - m.BaselinePrivateMb} MB");
        File.Delete(file);
    }

    private static void WriteWav(string path, int seconds)
    {
        var second = new byte[Rate * 2];
        for (int i = 0; i < Rate; i++) BinaryPrimitives.WriteInt16LittleEndian(second.AsSpan(i * 2), (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / Rate)));
        long data = (long)second.Length * seconds;
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        w.Write("RIFF"u8); w.Write((int)(36 + data)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write((int)data);
        for (int s = 0; s < seconds; s++) w.Write(second);
    }
}
