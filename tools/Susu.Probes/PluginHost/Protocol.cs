using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Susu.Probes.PluginHost;

/// <summary>
/// F00 prototype of the plugin IPC envelope (ARCHITECTURE 6): 32-bit little-endian
/// length + UTF-8 JSON, one frame at most 1 MiB, unknown types rejected.
/// </summary>
internal sealed record Frame(
    int ProtocolVersion,
    string Type,
    string? RequestId = null,
    string? PluginId = null,
    long Sequence = 0,
    string? Grant = null,
    JsonElement? Payload = null);

internal static class FrameTypes
{
    public const string Hello = "Hello", Load = "Load", Loaded = "Loaded", Invoke = "Invoke", ApiCall = "ApiCall", ApiResult = "ApiResult",
        Completed = "Completed", Failed = "Failed", Cancel = "Cancel", RuntimeFault = "RuntimeFault", Log = "Log", Shutdown = "Shutdown", Stats = "Stats";
    private static readonly HashSet<string> known = [Hello, Load, Loaded, Invoke, ApiCall, ApiResult, Completed, Failed, Cancel, RuntimeFault, Log, Shutdown, Stats];
    public static bool IsKnown(string type) => known.Contains(type);
}

internal static class Protocol
{
    public const int Version = 1;
    public const int MaxFrame = 1024 * 1024;

    public static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement Element<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) => JsonSerializer.SerializeToElement(value, info);

    public static void Write(Stream stream, Frame frame, object gate)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(frame, PluginJson.Default.Frame);
        if (body.Length > MaxFrame) throw new InvalidDataException("Frame exceeds 1 MiB.");
        byte[] buffer = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, body.Length);
        body.CopyTo(buffer, 4);
        lock (gate) { stream.Write(buffer); stream.Flush(); }
    }

    /// <summary>Returns null at clean end of stream. Oversized or malformed frames throw.</summary>
    public static Frame? Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        if (!ReadExactly(stream, header)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrame) throw new InvalidDataException($"Frame length {length} outside 1..{MaxFrame}.");
        byte[] body = new byte[length];
        if (!ReadExactly(stream, body)) throw new EndOfStreamException("Truncated frame.");
        Frame frame = JsonSerializer.Deserialize(body, PluginJson.Default.Frame) ?? throw new InvalidDataException("Empty frame.");
        if (frame.ProtocolVersion != Version) throw new InvalidDataException($"Protocol version {frame.ProtocolVersion} not supported.");
        if (!FrameTypes.IsKnown(frame.Type)) throw new InvalidDataException($"Unknown frame type '{frame.Type}'.");
        return frame;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0) return total == 0 ? false : throw new EndOfStreamException("Truncated frame.");
            total += read;
        }
        return true;
    }
}

internal sealed record HelloPayload(string Nonce, string Role, string Engine, int ProcessId);
internal sealed record LoadPayload(string Directory, string Entry, int MemoryMiB);
internal sealed record LoadedPayload(bool Ok, string? Error, double Milliseconds, long EngineBytes);
internal sealed record InvokePayload(int CallId, string Capability, JsonElement Request, JsonElement Config);
internal sealed record ApiCallPayload(int ApiId, int CallId, string Op, JsonElement Args);
internal sealed record ApiResultPayload(int ApiId, bool Ok, JsonElement Value);
internal sealed record CompletedPayload(int CallId, bool Ok, JsonElement? Result, PluginErrorInfo? Error);
internal sealed record PluginErrorInfo(string Kind, string? Detail);
internal sealed record CancelPayload(int CallId);
internal sealed record FaultPayload(string Reason, int[] FailedCalls, double RebuildMilliseconds);
internal sealed record StatsPayload(long EngineBytes, int Runtimes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Frame))]
[JsonSerializable(typeof(HelloPayload))]
[JsonSerializable(typeof(LoadPayload))]
[JsonSerializable(typeof(LoadedPayload))]
[JsonSerializable(typeof(InvokePayload))]
[JsonSerializable(typeof(ApiCallPayload))]
[JsonSerializable(typeof(ApiResultPayload))]
[JsonSerializable(typeof(CompletedPayload))]
[JsonSerializable(typeof(CancelPayload))]
[JsonSerializable(typeof(FaultPayload))]
[JsonSerializable(typeof(StatsPayload))]
[JsonSerializable(typeof(JsonElement))]
internal partial class PluginJson : JsonSerializerContext;
