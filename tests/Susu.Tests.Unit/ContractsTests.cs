using System.Text;
using System.Text.Json;
using Susu.Contracts;
using Susu.ContractsGen;
using Xunit;

namespace Susu.Tests.Unit;

public class IpcCodecTests
{
    private static IpcDecodeError Decode(string json, out IpcEnvelope? envelope) => IpcCodec.TryDecode(Encoding.UTF8.GetBytes(json), out envelope);

    [Fact]
    public void Valid_business_frame_decodes()
    {
        var error = Decode("""{"protocolVersion":1,"type":"Invoke","requestId":"r1","jobId":"j1","generation":3,"sequence":1,"payload":{"capability":"translate"}}""", out var envelope);
        Assert.Equal(IpcDecodeError.None, error);
        Assert.Equal(IpcMessageType.Invoke, envelope!.Type);
        Assert.Equal(3, envelope.Generation);
    }

    [Theory]
    [InlineData("""{"protocolVersion":1,"type":"Evil","requestId":"r","jobId":"j","sequence":1,"payload":{}}""", IpcDecodeError.UnknownType)]
    [InlineData("""{"protocolVersion":1,"type":"invoke","requestId":"r","jobId":"j","sequence":1,"payload":{}}""", IpcDecodeError.UnknownType)]
    [InlineData("""{"protocolVersion":1,"type":"3","requestId":"r","jobId":"j","sequence":1,"payload":{}}""", IpcDecodeError.UnknownType)]
    [InlineData("""{"protocolVersion":2,"type":"Invoke","requestId":"r","jobId":"j","sequence":1,"payload":{}}""", IpcDecodeError.UnsupportedVersion)]
    [InlineData("""{"type":"Invoke"}""", IpcDecodeError.MissingField)]
    [InlineData("""{"protocolVersion":1,"type":"Invoke","jobId":"j","sequence":1,"payload":{}}""", IpcDecodeError.MissingField)]
    [InlineData("""{"protocolVersion":1,"type":"Chunk","requestId":"r","jobId":"j","sequence":0,"payload":{}}""", IpcDecodeError.MissingField)]
    [InlineData("""{"protocolVersion":1,"type":"ApiCall","requestId":"r","jobId":"j","sequence":1,"payload":{}}""", IpcDecodeError.MissingField)]
    [InlineData("""{"protocolVersion":1,"type":"Invoke","requestId":"r","jobId":"j","sequence":1,"payload":{},"extra":true}""", IpcDecodeError.MalformedJson)]
    [InlineData("""[1,2,3]""", IpcDecodeError.MalformedJson)]
    [InlineData("""{"protocolVersion":1,"type":"Invoke" """, IpcDecodeError.MalformedJson)]
    public void Invalid_frames_are_rejected_without_executing(string json, IpcDecodeError expected)
    {
        Assert.Equal(expected, Decode(json, out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void Oversized_frame_is_rejected_before_parsing()
    {
        var big = new byte[ProtocolLimits.MaxFrameBytes + 1];
        Assert.Equal(IpcDecodeError.TooLarge, IpcCodec.TryDecode(big, out _));
        Assert.Throws<InvalidOperationException>(() => IpcCodec.Encode(new IpcEnvelope(1, IpcMessageType.Shutdown, Payload: JsonSerializer.SerializeToElement(new string('x', ProtocolLimits.MaxFrameBytes)))));
    }

    private static IpcEnvelope BigResult(int chars) => new(1, IpcMessageType.ApiResult, "r", "j", PluginId: "p",
        Payload: JsonSerializer.SerializeToElement(new { apiId = 1, ok = true, value = new { body = new string('a', chars) } }));

    private static List<IpcEnvelope> Frames(IpcEnvelope envelope)
    {
        long seq = 0;
        using var stream = new MemoryStream();
        IpcTransport.WriteFramed(stream, envelope, new object(), () => ++seq);
        stream.Position = 0;
        var frames = new List<IpcEnvelope>();
        while (IpcTransport.Read(stream) is { } frame) frames.Add(frame);
        return frames;
    }

    [Fact] // PLAN 4.5.4 item 4: JSON over one frame is split by transferId/index and reassembled
    public void Payload_over_one_frame_is_split_and_reassembled()
    {
        var envelope = BigResult(2_500_000);
        var frames = Frames(envelope);
        Assert.True(frames.Count > 1);
        Assert.All(frames, f => Assert.NotNull(f.Part));
        Assert.Equal(Enumerable.Range(1, frames.Count).Select(i => (long)i), frames.Select(f => f.Sequence));
        var reassembler = new IpcReassembler();
        IpcEnvelope? whole = null;
        foreach (var frame in frames) whole = reassembler.Accept(frame) ?? whole;
        Assert.NotNull(whole);
        Assert.Null(whole!.Part);
        Assert.Equal(0, reassembler.Open);
        Assert.Equal(envelope.Payload!.Value.GetRawText(), whole.Payload!.Value.GetRawText());
        Assert.Equal(("r", "j", "p", IpcMessageType.ApiResult), (whole.RequestId, whole.JobId, whole.PluginId, whole.Type));
        // A frame that fits is written unchanged.
        Assert.Null(Assert.Single(Frames(BigResult(10))).Part);
    }

    [Fact] // over the 4 MiB reassembled limit nothing is written and the caller gets a typed error
    public void Payload_over_the_transfer_limit_is_refused_before_writing()
    {
        using var stream = new MemoryStream();
        Assert.Throws<IpcPayloadTooLargeException>(() => IpcTransport.WriteFramed(stream, BigResult(4_300_000), new object(), () => 1));
        Assert.Equal(0, stream.Length);
    }

    [Fact] // parts cannot be reordered, re-bound to another request or used to exceed 4 MiB
    public void Reassembly_rejects_out_of_order_and_rebound_parts()
    {
        var frames = Frames(BigResult(2_500_000));
        Assert.Throws<InvalidDataException>(() => new IpcReassembler().Accept(frames[1]));
        var reassembler = new IpcReassembler();
        Assert.Null(reassembler.Accept(frames[0]));
        Assert.Throws<InvalidDataException>(() => reassembler.Accept(frames[1] with { RequestId = "other" }));
        var tooMany = frames[0] with { Part = frames[0].Part! with { Count = IpcFraming.MaxParts + 1 } };
        Assert.Equal(IpcDecodeError.MalformedJson, IpcCodec.TryDecode(IpcCodec.Encode(tooMany).AsSpan(4), out _));
    }

    [Fact]
    public void Encode_prefixes_little_endian_length_and_roundtrips()
    {
        var frame = IpcCodec.Encode(new IpcEnvelope(1, IpcMessageType.Cancel, "r", "j", 1, 7, Payload: JsonSerializer.SerializeToElement(new { attemptId = "a" })));
        int length = BitConverter.ToInt32(frame, 0);
        Assert.Equal(frame.Length - 4, length);
        Assert.Equal(IpcDecodeError.None, IpcCodec.TryDecode(frame.AsSpan(4), out var back));
        Assert.Equal(7, back!.Sequence);
    }

    [Fact]
    public void Compatibility_samples_decode_as_declared()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "protocol", "ipc", "samples");
        var files = Directory.GetFiles(folder, "*.json");
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            var error = IpcCodec.TryDecode(File.ReadAllBytes(file), out _);
            if (name.StartsWith("valid-", StringComparison.Ordinal)) Assert.True(error == IpcDecodeError.None, $"{name}: {error}");
            else Assert.True(error.ToString() == name.Split('.')[^1], $"{name}: got {error}");
        }
    }
}

public class NegotiationTests
{
    [Fact]
    public void Accepts_matching_version_role_and_build()
        => Assert.True(ProtocolNegotiation.Negotiate(ProtocolVersions.SupportedIpc, 1, "plugin-host", "plugin-host", "b1", "b1").Accepted);

    [Theory]
    [InlineData(2, "plugin-host", "b1", "version")]
    [InlineData(1, "selection-host", "b1", "role")]
    [InlineData(1, "plugin-host", "b0", "build")]
    public void Rejects_incompatible_peers(int version, string role, string build, string reason)
    {
        var result = ProtocolNegotiation.Negotiate(ProtocolVersions.SupportedIpc, version, "plugin-host", role, "b1", build);
        Assert.False(result.Accepted);
        Assert.Contains(reason, result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}

public class UiCommandTests
{
    [Theory]
    [InlineData(WindowKind.Main, UiCommands.SubmitText, true)]
    [InlineData(WindowKind.Settings, UiCommands.SecretWriteNew, true)]
    [InlineData(WindowKind.Main, UiCommands.SecretWriteNew, false)]
    [InlineData(WindowKind.Selection, UiCommands.SecretDelete, false)]
    [InlineData(WindowKind.Main, UiCommands.SettingsSave, false)]
    [InlineData(WindowKind.Tray, UiCommands.SubmitText, false)]
    [InlineData(WindowKind.Main, "Http.Fetch", false)]
    [InlineData(WindowKind.Main, UiCommands.Collect, true)]
    [InlineData(WindowKind.Settings, UiCommands.Collect, false)]
    [InlineData(WindowKind.Settings, UiCommands.VocabExport, true)]
    [InlineData(WindowKind.Settings, UiCommands.VocabResolve, true)]
    [InlineData(WindowKind.Settings, UiCommands.VocabSync, true)]
    [InlineData(WindowKind.Main, UiCommands.VocabExport, false)]
    [InlineData(WindowKind.Voice, UiCommands.VocabResolve, false)]
    [InlineData(WindowKind.Settings, null, false)]
    public void Commands_are_whitelisted_per_window(WindowKind window, string? command, bool allowed)
        => Assert.Equal(allowed, UiCommands.IsAllowed(window, command));

    [Fact]
    public void Only_settings_may_touch_secrets()
    {
        foreach (var command in UiCommands.All.Where(c => c.StartsWith("Secret.", StringComparison.Ordinal)))
            foreach (var window in Enum.GetValues<WindowKind>())
                Assert.Equal(window == WindowKind.Settings, UiCommands.IsAllowed(window, command));
    }
}

public class ErrorKindTests
{
    [Theory]
    [InlineData("auth", ErrorKind.Auth)]
    [InlineData("rate_limited", ErrorKind.RateLimited)]
    [InlineData("cancelled", ErrorKind.BadResponse)]
    [InlineData("whatever", ErrorKind.BadResponse)]
    [InlineData(null, ErrorKind.BadResponse)]
    public void Plugin_kinds_outside_the_list_are_bad_response(string? kind, ErrorKind expected) => Assert.Equal(expected, ErrorKinds.FromPlugin(kind));

    [Fact]
    public void Error_kinds_serialize_with_plugin_names()
        => Assert.Equal("\"rate_limited\"", JsonSerializer.Serialize(ErrorKind.RateLimited, ContractsJson.Default.Options));
}

public class AsrValidationTests
{
    private static AsrResult Segments(params (double Start, double End, string Text)[] s) => new("segments", Segments: [.. s.Select(x => new AsrSegment(x.Start, x.End, x.Text))]);

    [Fact]
    public void Overlapping_legal_segments_are_kept()
        => Assert.Null(PluginResultValidation.ValidateSegments(Segments((0, 2, "a"), (1.5, 3, "b")), "segments", 10));

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(-1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(0, 11)]
    public void Invalid_times_are_bad_response(double start, double end)
        => Assert.Equal(ErrorKind.BadResponse, PluginResultValidation.ValidateSegments(Segments((start, end, "x")), "segments", 10)!.Kind);

    [Fact]
    public void Unordered_empty_text_and_kind_mismatch_are_rejected()
    {
        Assert.NotNull(PluginResultValidation.ValidateSegments(Segments((5, 6, "a"), (1, 2, "b")), "segments", 10));
        Assert.NotNull(PluginResultValidation.ValidateSegments(Segments((1, 2, " ")), "segments", 10));
        Assert.NotNull(PluginResultValidation.ValidateSegments(new AsrResult("text", Text: "hi"), "segments", 10));
        Assert.Null(PluginResultValidation.ValidateSegments(new AsrResult("text", Text: "hi"), "text", 10));
    }
}

public class TypeScriptSnapshotTests
{
    [Fact]
    public void Generated_typescript_matches_committed_files()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "protocol", "generated");
        foreach (var (name, content) in TypeScriptGenerator.Generate())
        {
            string path = Path.Combine(folder, name);
            Assert.True(File.Exists(path), $"{name} missing: run tools/Susu.ContractsGen");
            // Normalize both sides: with core.autocrlf the generator's own raw-string header is checked
            // out with CRLF too, so the generated side can carry \r\n as well as the committed file.
            Assert.Equal(content.Replace("\r\n", "\n"), File.ReadAllText(path).Replace("\r\n", "\n"));
        }
    }

    [Fact]
    public void Plugin_declarations_expose_every_capability_result()
    {
        string dts = TypeScriptGenerator.Generate()["susu-plugin.d.ts"];
        foreach (string type in new[] { "TranslateResult", "DictionaryResult", "OcrResult", "AsrResult", "VocabResult", "OptionsResult", "SusuPlugin" })
            Assert.Contains($"interface {type}", dts, StringComparison.Ordinal);
    }
}
