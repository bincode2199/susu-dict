using System.Text.Json;
using System.Text.Json.Serialization;

namespace Susu.Contracts;

/// <summary>Closed set of plugin-host IPC messages (ARCHITECTURE 6). Unknown names fail to decode.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<IpcMessageType>))]
public enum IpcMessageType
{
    Hello, Load, Loaded, Invoke, ApiCall, ApiResult, Chunk, Ack, Cancel, Completed, Failed, RuntimeFault, Shutdown,
}

/// <summary>
/// IPC envelope: 32-bit little-endian length + UTF-8 JSON of this record. Business messages carry
/// request/job/generation/sequence; ApiCall additionally carries the host-issued grant.
/// </summary>
public sealed record IpcEnvelope(
    int ProtocolVersion,
    IpcMessageType Type,
    string? RequestId = null,
    string? JobId = null,
    long Generation = 0,
    long Sequence = 0,
    string? PluginId = null,
    string? Grant = null,
    JsonElement? Payload = null);

public enum IpcDecodeError { None, Empty, TooLarge, MalformedJson, UnsupportedVersion, UnknownType, MissingField }

public static class IpcCodec
{
    private static readonly HashSet<IpcMessageType> business =
        [IpcMessageType.Invoke, IpcMessageType.ApiCall, IpcMessageType.ApiResult, IpcMessageType.Chunk, IpcMessageType.Ack,
         IpcMessageType.Cancel, IpcMessageType.Completed, IpcMessageType.Failed];

    public static bool IsBusiness(IpcMessageType type) => business.Contains(type);

    public static byte[] Encode(IpcEnvelope envelope)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(envelope, ContractsJson.Default.IpcEnvelope);
        if (body.Length > ProtocolLimits.MaxFrameBytes) throw new InvalidOperationException("IPC frame exceeds 1 MiB.");
        var frame = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Validates one frame body (without the length prefix). Nothing is executed for a rejected frame.</summary>
    public static IpcDecodeError TryDecode(ReadOnlySpan<byte> body, out IpcEnvelope? envelope)
    {
        envelope = null;
        if (body.IsEmpty) return IpcDecodeError.Empty;
        if (body.Length > ProtocolLimits.MaxFrameBytes) return IpcDecodeError.TooLarge;
        // Version and type are checked before full deserialization so unknown types are distinguishable.
        try
        {
            var reader = new Utf8JsonReader(body);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return IpcDecodeError.MalformedJson;
            if (!root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v)) return IpcDecodeError.MissingField;
            if (!ProtocolVersions.SupportedIpc.Contains(v)) return IpcDecodeError.UnsupportedVersion;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !Enum.TryParse<IpcMessageType>(type.GetString(), ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed) || type.GetString() != parsed.ToString())
                return IpcDecodeError.UnknownType;
            envelope = root.Deserialize(ContractsJson.Default.IpcEnvelope);
        }
        catch (JsonException) { return IpcDecodeError.MalformedJson; }
        if (envelope is null) return IpcDecodeError.MalformedJson;
        if (IsBusiness(envelope.Type))
        {
            if (string.IsNullOrEmpty(envelope.RequestId) || string.IsNullOrEmpty(envelope.JobId) || envelope.Sequence <= 0 || envelope.Payload is null)
            { envelope = null; return IpcDecodeError.MissingField; }
            if (envelope.Type == IpcMessageType.ApiCall && string.IsNullOrEmpty(envelope.Grant)) { envelope = null; return IpcDecodeError.MissingField; }
        }
        return IpcDecodeError.None;
    }
}

/// <summary>Frames <see cref="IpcEnvelope"/> over a byte stream (32-bit LE length + UTF-8 JSON body).</summary>
public static class IpcTransport
{
    public static void Write(Stream stream, IpcEnvelope envelope, object gate)
    {
        byte[] frame = IpcCodec.Encode(envelope);
        lock (gate) { stream.Write(frame); stream.Flush(); }
    }

    /// <summary>Returns null at a clean end of stream. Malformed or oversized frames throw <see cref="InvalidDataException"/>.</summary>
    public static IpcEnvelope? Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        if (!ReadExactly(stream, header)) return null;
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > ProtocolLimits.MaxFrameBytes) throw new InvalidDataException($"Frame length {length} outside 1..{ProtocolLimits.MaxFrameBytes}.");
        byte[] body = new byte[length];
        if (!ReadExactly(stream, body)) throw new EndOfStreamException("Truncated frame.");
        var error = IpcCodec.TryDecode(body, out var envelope);
        if (error != IpcDecodeError.None || envelope is null) throw new InvalidDataException($"Malformed IPC frame: {error}.");
        return envelope;
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

public sealed record HelloPayload(string Role, string HostBuild, string LaunchNonce, int ProcessId);
public sealed record ChunkPayload(string AttemptId, long Index, string Text);
public sealed record AckPayload(string AttemptId, long Index, int Bytes);

// ---- Plugin-host business payloads (F04.1-F04.3). Carried in IpcEnvelope.Payload. ----

/// <summary>Main → child: load one package into a fresh runtime slot.</summary>
public sealed record LoadPayload(string Directory, string Entry, int MemoryMiB);
/// <summary>Child → main: result of a Load request (JobId carries the plugin id, ARCHITECTURE 6).</summary>
public sealed record LoadedPayload(bool Ok, string? Error, double Milliseconds, long EngineBytes);
/// <summary>Main → child: invoke one capability of an already-loaded plugin.</summary>
public sealed record InvokePayload(int CallId, string Capability, JsonElement Request, JsonElement Config);
/// <summary>Child → main: the plugin asks the host to perform a bounded operation (http/store/...).</summary>
public sealed record ApiCallPayload(int ApiId, int CallId, string Op, JsonElement Args);
/// <summary>Main → child: the host's answer to one ApiCall.</summary>
public sealed record ApiResultPayload(int ApiId, bool Ok, JsonElement Value);
/// <summary>Child → main: terminal result of one Invoke (Completed/Failed message types).</summary>
public sealed record CompletedPayload(int CallId, bool Ok, JsonElement? Result, PluginErrorInfo? Error);
public sealed record PluginErrorInfo(string Kind, string? Detail);
/// <summary>Main → child: cancel one in-flight call; host-side cancellation is authoritative.</summary>
public sealed record CancelPayload(int CallId);
/// <summary>Child → main: a runtime was rebuilt after its execution budget was exceeded.</summary>
public sealed record FaultPayload(string Reason, int[] FailedCalls, double RebuildMilliseconds);
/// <summary>Child → main: aggregate engine stats, polled for diagnostics.</summary>
public sealed record StatsPayload(long EngineBytes, int Runtimes);
/// <summary>Child → main: a plugin console.* message, rate-limited by the host (PLAN 4.5.4).</summary>
public sealed record PluginLogPayload(string Level, string Text);
