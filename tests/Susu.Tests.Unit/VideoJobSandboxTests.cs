using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;
using Susu.Jobs;
using Susu.Storage;
using Susu.Testing;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F14.2 end to end through the shipped OpenAI ASR package in the real sandbox against a loopback vendor: two slices go out as real
/// uploads, cue times land on the media time axis, a vendor quota answer parks the job and a resume re-sends only the slice that
/// was refused (T07, A05, B07). Needs the published host binary (reported as passing without running when it is absent, like
/// <see cref="F12VerificationTests"/>). The real vendor is not executed.
/// </summary>
public class VideoJobSandboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Segments = """{"segments":[{"start":0.5,"end":2,"text":"first"},{"start":3,"end":5,"text":"second"}]}""";

    [Fact]
    public async Task Real_package_transcribes_slices_with_offsets_and_resumes_after_a_vendor_quota_answer()
    {
        int hits = 0;
        var sizes = new List<int>();
        using var server = new LoopbackHttpServer(req =>
        {
            int n = Interlocked.Increment(ref hits);
            lock (sizes) sizes.Add(req.Body.Length);
            return n == 2
                ? LoopbackHttpResponse.Json(429, """{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}""")
                : LoopbackHttpResponse.Json(200, Segments);
        });
        using var rig = F12VerificationTests.Build(SpeechCatalog.OpenAiAsr, server);
        if (rig is null) return;
        using var root = new TempRoot();
        var decoder = new VideoJobTests.FakeDecoder(() => new VideoJobTests.FakeReader(
            new MediaProbe(TimeSpan.FromSeconds(20), true, [new MediaAudioStream(1, "aac", 44100, 2, true)]), VideoJobTests.Blocks(20, _ => true).GetEnumerator()));
        var tokens = new MediaTokens();
        string file = Path.Combine(root.Root, "clip.mp4"); File.WriteAllText(file, "x");
        var asr = rig.Provider("whisper-1");
        var tr = new VideoJobTests.FakeTranslator("tr", VideoJobTests.ItemsLimits);
        var jobs = new VideoJobs(tokens, decoder, rig.Files, () => asr, () => tr) { Confirmation = new VideoJobTests.Confirm() };
        var job = jobs.Start(tokens.Issue(file)!, "en", "zh", slicing: new MediaSliceOptions(TimeSpan.FromSeconds(10))).Job!;

        var parked = await Wait(job, s => s.Phase == VideoJobPhase.QuotaExhausted);
        Assert.Equal(VideoQuotaSide.Asr, parked.Quota);
        Assert.Contains(parked.Error!.Kind, new[] { ErrorKind.Quota, ErrorKind.RateLimited });
        Assert.Equal(2, hits);
        Assert.Equal(2, job.Cues().Count);

        Assert.True(job.SwitchAsr(rig.Provider("whisper-1")));
        Assert.True(job.Resume());
        Assert.Equal(VideoJobPhase.Done, (await Wait(job, s => s.IsTerminal)).Phase);
        Assert.Equal(3, hits); // slice 0 once, slice 1 refused once and sent once more
        Assert.Equal(sizes[1], sizes[2]); // the same slice again
        Assert.Equal([0.5, 3, 10.5, 13], job.Cues().Select(c => c.Start));
        Assert.All(job.Cues(), c => Assert.Equal("T:" + c.Original, c.Translation));
        await rig.AssertNoFilesLeftAsync();
    }

    private static async Task<VideoJobState> Wait(VideoJob job, Func<VideoJobState, bool> predicate)
    {
        var tcs = new TaskCompletionSource<VideoJobState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(VideoJobState s) { if (predicate(s)) tcs.TrySetResult(s); }
        job.StateChanged += On;
        try { On(job.State); return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct); }
        finally { job.StateChanged -= On; }
    }
}
