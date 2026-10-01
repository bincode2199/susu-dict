using System.Text;
using Susu.Abstractions;
using Susu.Jobs;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F14.3 subtitle validation and export (TEST-PLAN A06 and VID03). The formatter is pure; the exporter writes real files in a temp folder
/// and reads them back. The "golden" strings are written out by hand, not produced by the code under test.
/// </summary>
public class SubtitleExportTests : IDisposable
{
    private readonly TempRoot root = new();
    public void Dispose() => root.Dispose();

    private static string G(params string[] lines) => string.Join("\n", lines) + "\n";

    private static VideoCue Cue(string id, double s, double e, string o, string? t = null, string? err = null) => new(id, s, e, o, t, err);

    private static readonly VideoCue[] Golden =
    [
        Cue("c1", 0.5, 2, "Hello", "你好"),
        Cue("c2", 3.25, 5, "Line one\nLine two", "第一行\n第二行"),
        Cue("c3", 4.5, 6, "Speaker B", null, "bad"), // overlaps c2, translation failed
    ];

    private static SubtitleBuild Build(IReadOnlyList<VideoCue> cues, SubtitleMode mode, SubtitleFormat format, TimeSpan? duration = null) =>
        SubtitleFormatter.Build(cues, duration, mode, format);

    // ---------------- golden files ----------------

    [Fact]
    public void Original_srt_vtt_txt_golden_and_the_overlap_is_kept_as_given()
    {
        var srt = Build(Golden, SubtitleMode.Original, SubtitleFormat.Srt);
        Assert.Equal(G("1", "00:00:00,500 --> 00:00:02,000", "Hello", "", "2", "00:00:03,250 --> 00:00:05,000", "Line one", "Line two", "",
            "3", "00:00:04,500 --> 00:00:06,000", "Speaker B", ""), srt.Text);
        Assert.Equal((3, 1, 0), (srt.Exported, srt.Overlaps, srt.Issues.Count));

        Assert.Equal(G("WEBVTT", "", "00:00:00.500 --> 00:00:02.000", "Hello", "", "00:00:03.250 --> 00:00:05.000", "Line one", "Line two", "",
            "00:00:04.500 --> 00:00:06.000", "Speaker B", ""), Build(Golden, SubtitleMode.Original, SubtitleFormat.Vtt).Text);

        Assert.Equal(G("[00:00:00.500] Hello", "", "[00:00:03.250] Line one", "    Line two", "", "[00:00:04.500] Speaker B", ""),
            Build(Golden, SubtitleMode.Original, SubtitleFormat.Txt).Text);
    }

    [Fact]
    public void Translation_only_leaves_out_untranslated_cues_and_reports_them_without_substituting_the_original()
    {
        var srt = Build(Golden, SubtitleMode.Translation, SubtitleFormat.Srt);
        Assert.Equal(G("1", "00:00:00,500 --> 00:00:02,000", "你好", "", "2", "00:00:03,250 --> 00:00:05,000", "第一行", "第二行", ""), srt.Text);
        Assert.Equal([new SubtitleIssue("c3", SubtitleIssues.MissingTranslation)], srt.Issues);
        Assert.Equal(0, srt.Overlaps); // the overlapping cue is not in this file

        var vtt = Build(Golden, SubtitleMode.Translation, SubtitleFormat.Vtt);
        Assert.Equal(G("WEBVTT", "", "00:00:00.500 --> 00:00:02.000", "你好", "", "00:00:03.250 --> 00:00:05.000", "第一行", "第二行", ""), vtt.Text);
    }

    [Fact]
    public void Bilingual_both_orders_and_a_failed_cue_shows_its_original_only()
    {
        Assert.Equal(G("1", "00:00:00,500 --> 00:00:02,000", "Hello", "你好", "", "2", "00:00:03,250 --> 00:00:05,000", "Line one", "Line two", "第一行", "第二行", "",
            "3", "00:00:04,500 --> 00:00:06,000", "Speaker B", ""), Build(Golden, SubtitleMode.BilingualOriginalFirst, SubtitleFormat.Srt).Text);
        Assert.Equal(G("WEBVTT", "", "00:00:00.500 --> 00:00:02.000", "你好", "Hello", "", "00:00:03.250 --> 00:00:05.000", "第一行", "第二行", "Line one", "Line two", "",
            "00:00:04.500 --> 00:00:06.000", "Speaker B", ""), Build(Golden, SubtitleMode.BilingualTranslationFirst, SubtitleFormat.Vtt).Text);
        Assert.Equal(G("[00:00:00.500] 你好", "    Hello", "", "[00:00:03.250] 第一行", "    第二行", "    Line one", "    Line two", "", "[00:00:04.500] Speaker B", ""),
            Build(Golden, SubtitleMode.BilingualTranslationFirst, SubtitleFormat.Txt).Text);
        Assert.Empty(Build(Golden, SubtitleMode.BilingualOriginalFirst, SubtitleFormat.Srt).Issues); // not an error: shown as original only
    }

    // ---------------- text escaping ----------------

    [Fact]
    public void Vtt_escapes_markup_and_the_arrow_and_srt_breaks_the_arrow()
    {
        var cues = new[] { Cue("a", 1, 2, "a < b & c --> d <i>x</i>") };
        Assert.Equal(G("WEBVTT", "", "00:00:01.000 --> 00:00:02.000", "a &lt; b &amp; c --&gt; d &lt;i&gt;x&lt;/i&gt;", ""), Build(cues, SubtitleMode.Original, SubtitleFormat.Vtt).Text);
        string srt = Build(cues, SubtitleMode.Original, SubtitleFormat.Srt).Text!;
        Assert.False(srt.Contains("c --> d", StringComparison.Ordinal));
        Assert.Contains("c --\u200B> d", srt);
        Assert.Equal(2, srt.Split("-->").Length); // the timing line only
    }

    [Fact]
    public void Blank_lines_line_ends_and_control_characters_cannot_split_or_corrupt_a_cue()
    {
        var cues = new[] { Cue("a", 1, 2, "one\r\n\r\n\r\ntwo\rthree\u2028four\n   \n\tfive\u0007\u0000\uFEFF") };
        foreach (var f in new[] { SubtitleFormat.Srt, SubtitleFormat.Vtt })
        {
            string text = Build(cues, SubtitleMode.Original, f).Text!;
            Assert.DoesNotContain("\r", text);
            Assert.DoesNotContain("\n\n\n", text);
            Assert.Contains("\none\ntwo\nthree\nfour\nfive\n\n", text);
            Assert.DoesNotContain('\u0007', text);
            Assert.DoesNotContain('\uFEFF', text);
        }
    }

    [Fact]
    public void A_lone_surrogate_becomes_the_replacement_character_and_astral_characters_survive()
    {
        var cues = new[] { Cue("a", 1, 2, "x\uD800y 😀") };
        string text = Build(cues, SubtitleMode.Original, SubtitleFormat.Srt).Text!;
        Assert.Contains("x\uFFFDy 😀", text);
        Assert.Equal(text, new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(text))); // strict UTF-8 round trip
    }

    // ---------------- validation (A06) ----------------

    [Fact]
    public void Bad_cues_are_reported_and_left_out_and_the_good_ones_keep_their_times_and_get_contiguous_numbers()
    {
        var cues = new[]
        {
            Cue("ok1", 1, 2, "a"),
            Cue("nan", double.NaN, 3, "x"), Cue("nan2", 2, double.NaN, "x"), Cue("inf", 2, double.PositiveInfinity, "x"),
            Cue("neg", -1, 2, "x"), Cue("neg2", 2, -0.5, "x"),
            Cue("eq", 3, 3, "x"), Cue("rev", 5, 4, "x"),
            Cue("big", 2, 1e300, "x"),
            Cue("tiny", 7.0001, 7.0002, "x"), // collapses to one millisecond
            Cue("ok2", 8, 9, "b"),
            Cue("late", 7.5, 8.5, "x"), // starts before ok2
            Cue("ok2", 9, 10, "dup"),
            Cue("blank", 11, 12, " \r\n\t "),
            Cue("ok3", 12, 13, "c"),
        };
        var b = Build(cues, SubtitleMode.Original, SubtitleFormat.Srt);
        Assert.Equal(3, b.Exported);
        Assert.Equal(
            [("nan", SubtitleIssues.BadTime), ("nan2", SubtitleIssues.BadTime), ("inf", SubtitleIssues.BadTime), ("neg", SubtitleIssues.BadTime), ("neg2", SubtitleIssues.BadTime),
             ("eq", SubtitleIssues.BadRange), ("rev", SubtitleIssues.BadRange), ("big", SubtitleIssues.OutOfRange), ("tiny", SubtitleIssues.BadRange),
             ("late", SubtitleIssues.OutOfOrder), ("ok2", SubtitleIssues.DuplicateId), ("blank", SubtitleIssues.EmptyText)],
            b.Issues.Select(i => (i.CueId, i.Code)));
        Assert.Equal(G("1", "00:00:01,000 --> 00:00:02,000", "a", "", "2", "00:00:08,000 --> 00:00:09,000", "b", "", "3", "00:00:12,000 --> 00:00:13,000", "c", ""), b.Text);
    }

    [Fact]
    public void A_cue_past_the_media_duration_is_left_out_but_the_tolerance_for_decode_rounding_applies()
    {
        var cues = new[] { Cue("in", 8, 10.9, "a"), Cue("out", 9, 11.1, "b"), Cue("after", 12, 13, "c") };
        var b = Build(cues, SubtitleMode.Original, SubtitleFormat.Vtt, TimeSpan.FromSeconds(10));
        Assert.Equal(1, b.Exported);
        Assert.Equal([("out", SubtitleIssues.OutOfRange), ("after", SubtitleIssues.OutOfRange)], b.Issues.Select(i => (i.CueId, i.Code)));
        Assert.Equal(3, Build(cues, SubtitleMode.Original, SubtitleFormat.Vtt).Exported); // no duration known: only the absurd-value limit applies
    }

    [Fact]
    public void Legitimate_overlap_and_a_contained_cue_are_kept_untouched_and_counted()
    {
        var cues = new[] { Cue("a", 1, 10, "long"), Cue("b", 2, 3, "inside"), Cue("c", 2.5, 4, "second speaker"), Cue("d", 10, 11, "touching") };
        var b = Build(cues, SubtitleMode.Original, SubtitleFormat.Srt);
        Assert.Equal(4, b.Exported);
        Assert.Equal(2, b.Overlaps); // b and c start inside a; d starts exactly when a ends and is not an overlap
        Assert.Empty(b.Issues);
        Assert.Contains("00:00:01,000 --> 00:00:10,000", b.Text);
        Assert.Contains("00:00:02,000 --> 00:00:03,000", b.Text);
        Assert.Contains("00:00:02,500 --> 00:00:04,000", b.Text);
    }

    [Fact]
    public void Identical_start_times_are_in_order_and_whitespace_translation_counts_as_missing()
    {
        var cues = new[] { Cue("a", 1, 2, "x", "  "), Cue("b", 1, 2, "y", "Y") };
        Assert.Equal(2, Build(cues, SubtitleMode.Original, SubtitleFormat.Srt).Exported);
        var t = Build(cues, SubtitleMode.Translation, SubtitleFormat.Srt);
        Assert.Equal(1, t.Exported);
        Assert.Equal([new SubtitleIssue("a", SubtitleIssues.MissingTranslation)], t.Issues);
    }

    [Fact]
    public void Nothing_exportable_gives_no_text()
    {
        Assert.Null(Build([], SubtitleMode.Original, SubtitleFormat.Srt).Text);
        Assert.Null(Build([Cue("a", 2, 1, "x")], SubtitleMode.Original, SubtitleFormat.Vtt).Text);
        Assert.Null(Build([Cue("a", 1, 2, "x")], SubtitleMode.Translation, SubtitleFormat.Txt).Text);
    }

    // ---------------- timestamps ----------------

    [Theory]
    [InlineData(0.0, ',', "00:00:00,000")]
    [InlineData(1.9996, '.', "00:00:02.000")]
    [InlineData(59.9996, ',', "00:01:00,000")]
    [InlineData(2520.5, ',', "00:42:00,500")]
    [InlineData(3599.999, '.', "00:59:59.999")]
    [InlineData(3600, ',', "01:00:00,000")]
    [InlineData(360000.25, ',', "100:00:00,250")]
    public void Timestamp_formats_use_comma_for_srt_dot_for_vtt_and_never_wrap_hours(double seconds, char sep, string expected)
    {
        var cue = new[] { Cue("a", seconds, seconds + 1, "x") };
        var f = sep == ',' ? SubtitleFormat.Srt : SubtitleFormat.Vtt;
        Assert.Contains(expected + " --> ", Build(cue, SubtitleMode.Original, f).Text);
    }

    [Fact]
    public void A_42_minute_cue_exports_with_exact_times()
    {
        var cues = new[] { Cue("a", 2519.123, 2523.987, "x") };
        Assert.Contains("00:41:59,123 --> 00:42:03,987", Build(cues, SubtitleMode.Original, SubtitleFormat.Srt, TimeSpan.FromMinutes(42.1)).Text);
    }

    // ---------------- file names ----------------

    [Theory]
    [InlineData("movie.mp4", SubtitleMode.Original, SubtitleFormat.Srt, "movie.original.srt")]
    [InlineData("a:b*c?d\"e|f<g>.mkv", SubtitleMode.Translation, SubtitleFormat.Vtt, "a_b_c_d_e_f_g_.translation.vtt")]
    [InlineData("C:\\videos\\..\\clip.final.mp4", SubtitleMode.BilingualOriginalFirst, SubtitleFormat.Txt, "clip.final.bilingual.txt")]
    [InlineData("CON.mp4", SubtitleMode.Original, SubtitleFormat.Srt, "_CON.original.srt")]
    [InlineData("nul", SubtitleMode.Original, SubtitleFormat.Srt, "_nul.original.srt")]
    [InlineData("com1.part.avi", SubtitleMode.Original, SubtitleFormat.Srt, "_com1.part.original.srt")]
    [InlineData("", SubtitleMode.BilingualTranslationFirst, SubtitleFormat.Srt, "subtitles.bilingual-translation-first.srt")]
    [InlineData(null, SubtitleMode.Original, SubtitleFormat.Srt, "subtitles.original.srt")]
    [InlineData("...", SubtitleMode.Original, SubtitleFormat.Srt, "subtitles.original.srt")]
    [InlineData("trail. .mp4", SubtitleMode.Original, SubtitleFormat.Srt, "trail.original.srt")]
    public void Default_file_names_are_safe(string? display, SubtitleMode mode, SubtitleFormat format, string expected) =>
        Assert.Equal(expected, SubtitleFileName.Make(display, mode, format));

    [Fact]
    public void A_long_name_is_cut_without_splitting_a_surrogate_pair_and_has_no_invalid_characters()
    {
        string name = new string('a', 79) + "😀tail.mp4";
        string made = SubtitleFileName.Make(name, SubtitleMode.Original, SubtitleFormat.Srt);
        Assert.Equal(new string('a', 79) + ".original.srt", made);
        Assert.Equal(made, new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(made)));
        Assert.True(made.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    // ---------------- exporter: files ----------------

    private static VideoJobResult Result(IReadOnlyList<VideoCue> cues, TimeSpan? duration = null, string name = "movie.mp4") =>
        new("j1", name, new VideoJobState("j1", VideoJobPhase.Done), cues, duration);

    private SubtitleExporter Exporter(VideoJobResult? r, ISubtitleSavePicker? picker = null, Func<string, Stream>? temp = null) =>
        new(id => id == "j1" ? r : null, picker, temp);

    private string Dir() { string d = Path.Combine(root.Root, "out"); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void Export_writes_utf8_without_bom_and_replaces_an_existing_file_leaving_no_temp()
    {
        string target = Path.Combine(Dir(), "x.srt");
        File.WriteAllText(target, "old");
        var r = Exporter(Result(Golden)).ExportTo("j1", SubtitleMode.Translation, SubtitleFormat.Srt, target);
        Assert.True(r.Ok);
        Assert.Equal((target, 2, 1), (r.Path, r.Exported, r.Issues.Count));
        byte[] bytes = File.ReadAllBytes(target);
        Assert.NotEqual(0xEF, bytes[0]); // no BOM
        Assert.Equal(Build(Golden, SubtitleMode.Translation, SubtitleFormat.Srt).Text, new UTF8Encoding(false, true).GetString(bytes));
        Assert.Equal([target], Directory.GetFileSystemEntries(Dir()));
    }

    [Fact]
    public void Nothing_to_export_writes_no_file_and_a_bad_path_is_refused()
    {
        string dir = Dir();
        var bad = Exporter(Result([Cue("a", 3, 2, "x")]));
        var r = bad.ExportTo("j1", SubtitleMode.Original, SubtitleFormat.Srt, Path.Combine(dir, "x.srt"));
        Assert.Equal((false, SubtitleExportErrors.NothingToExport), (r.Ok, r.Error));
        Assert.Equal([new SubtitleIssue("a", SubtitleIssues.BadRange)], r.Issues);
        Assert.Empty(Directory.GetFileSystemEntries(dir));

        var good = Exporter(Result(Golden));
        Assert.Equal(SubtitleExportErrors.PathInvalid, good.ExportTo("j1", SubtitleMode.Original, SubtitleFormat.Srt, Path.Combine(dir, "missing", "x.srt")).Error);
        Assert.Equal(SubtitleExportErrors.PathInvalid, good.ExportTo("j1", SubtitleMode.Original, SubtitleFormat.Srt, dir).Error); // a directory
        Assert.Equal(SubtitleExportErrors.PathInvalid, good.ExportTo("j1", SubtitleMode.Original, SubtitleFormat.Srt, "bad\0name.srt").Error);
        Assert.Equal(SubtitleExportErrors.UnknownJob, good.ExportTo("nope", SubtitleMode.Original, SubtitleFormat.Srt, Path.Combine(dir, "x.srt")).Error);
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    private sealed class TornStream(string path, int errorCode) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            base.Write(buffer[..(buffer.Length / 2)]);
            Flush();
            throw new IOException("disk", errorCode);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070), true, SubtitleExportErrors.DiskFull)]
    [InlineData(unchecked((int)0x80070027), true, SubtitleExportErrors.DiskFull)]
    [InlineData(unchecked((int)0x80070005), false, SubtitleExportErrors.WriteFailed)]
    public void A_write_that_fails_halfway_is_recoverable_when_the_disk_is_full_and_leaves_no_partial_file(int hresult, bool retryable, string code)
    {
        string target = Path.Combine(Dir(), "x.vtt");
        File.WriteAllText(target, "previous");
        var r = Exporter(Result(Golden), temp: p => new TornStream(p, hresult)).ExportTo("j1", SubtitleMode.Original, SubtitleFormat.Vtt, target);
        Assert.Equal((false, code, retryable, null), (r.Ok, r.Error, r.Retryable, r.Path));
        Assert.Equal("previous", File.ReadAllText(target)); // untouched
        Assert.Equal([target], Directory.GetFileSystemEntries(Dir())); // no temp left

        // The same export succeeds once there is room (retry), over the same target.
        Assert.True(Exporter(Result(Golden)).ExportTo("j1", SubtitleMode.Original, SubtitleFormat.Vtt, target).Ok);
        Assert.StartsWith("WEBVTT\n", File.ReadAllText(target));
    }

    [Fact]
    public void A_disk_full_on_a_new_target_leaves_the_folder_empty_and_a_locked_target_is_a_write_failure()
    {
        string dir = Dir();
        var r = Exporter(Result(Golden), temp: p => new TornStream(p, unchecked((int)0x80070070))).ExportTo("j1", SubtitleMode.Original, SubtitleFormat.Srt, Path.Combine(dir, "n.srt"));
        Assert.Equal(SubtitleExportErrors.DiskFull, r.Error);
        Assert.Empty(Directory.GetFileSystemEntries(dir));

        string locked = Path.Combine(dir, "locked.srt");
        File.WriteAllText(locked, "keep");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read)) // an open reader blocks replacing it
        {
            var l = Exporter(Result(Golden)).ExportTo("j1", SubtitleMode.Original, SubtitleFormat.Srt, locked);
            Assert.Equal((false, SubtitleExportErrors.WriteFailed), (l.Ok, l.Error));
        }
        Assert.Equal("keep", File.ReadAllText(locked));
        Assert.Equal([locked], Directory.GetFileSystemEntries(dir));
    }

    private sealed class Picker(string? answer) : ISubtitleSavePicker
    {
        public string? Name, FormatSeen;
        public Task<string?> PickAsync(string suggestedFileName, SubtitleFormat format, CancellationToken ct) { Name = suggestedFileName; FormatSeen = format.ToString(); return Task.FromResult(answer); }
    }

    [Fact]
    public async Task The_dialog_gets_a_safe_suggested_name_and_cancelling_it_writes_nothing()
    {
        string dir = Dir();
        var cancelled = new Picker(null);
        var r = await Exporter(Result(Golden, name: "a:b.mp4"), cancelled).ExportAsync("j1", SubtitleMode.BilingualOriginalFirst, SubtitleFormat.Vtt, TestContext.Current.CancellationToken);
        Assert.True(r.Cancelled);
        Assert.Equal(("a_b.bilingual.vtt", "Vtt"), (cancelled.Name, cancelled.FormatSeen));
        Assert.Empty(Directory.GetFileSystemEntries(dir));

        var chosen = new Picker(Path.Combine(dir, "out.vtt"));
        var ok = await Exporter(Result(Golden), chosen).ExportAsync("j1", SubtitleMode.Original, SubtitleFormat.Vtt, TestContext.Current.CancellationToken);
        Assert.True(ok.Ok);
        Assert.True(File.Exists(Path.Combine(dir, "out.vtt")));
        Assert.Equal(SubtitleExportErrors.NoPicker, (await Exporter(Result(Golden)).ExportAsync("j1", SubtitleMode.Original, SubtitleFormat.Vtt, TestContext.Current.CancellationToken)).Error);
        Assert.Equal(SubtitleExportErrors.UnknownJob, (await Exporter(Result(Golden), chosen).ExportAsync("zz", SubtitleMode.Original, SubtitleFormat.Vtt, TestContext.Current.CancellationToken)).Error);
    }

    [Fact]
    public void The_duration_in_the_result_is_used_for_the_range_check_when_exporting()
    {
        string target = Path.Combine(Dir(), "d.srt");
        var cues = new[] { Cue("in", 1, 2, "a"), Cue("out", 50, 51, "b") };
        var r = Exporter(Result(cues, TimeSpan.FromSeconds(10))).ExportTo("j1", SubtitleMode.Original, SubtitleFormat.Srt, target);
        Assert.True(r.Ok);
        Assert.Equal([new SubtitleIssue("out", SubtitleIssues.OutOfRange)], r.Issues);
        Assert.DoesNotContain("00:00:50", File.ReadAllText(target));
    }
}
