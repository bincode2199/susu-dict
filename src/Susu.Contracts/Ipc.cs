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
    JsonElement? Payload = null,
    IpcFramePart? Part = null);

/// <summary>
/// Marks one piece of a payload too large for a single frame (PLAN 4.5.4 item 4: plain JSON over one
/// frame is split by transferId/index and reassembled to at most 4 MiB). A part envelope keeps the
/// original type and request/job/plugin/grant identity; its Payload is a JSON string holding the Base64
/// of this slice of the original payload's UTF-8 JSON. See <see cref="IpcFraming"/>.
/// </summary>
public sealed record IpcFramePart(string TransferId, int Index, int Count);

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
        if (envelope.Part is { } part && (string.IsNullOrEmpty(part.TransferId) || part.TransferId.Length > 64 || part.Count < 2
            || part.Count > IpcFraming.MaxParts || part.Index < 0 || part.Index >= part.Count || envelope.Payload is not { ValueKind: JsonValueKind.String }))
        { envelope = null; return IpcDecodeError.MalformedJson; }
        if (IsBusiness(envelope.Type))
        {
            if (string.IsNullOrEmpty(envelope.RequestId) || string.IsNullOrEmpty(envelope.JobId) || envelope.Sequence <= 0 || envelope.Payload is null)
            { envelope = null; return IpcDecodeError.MissingField; }
            if (envelope.Type == IpcMessageType.ApiCall && string.IsNullOrEmpty(envelope.Grant)) { envelope = null; return IpcDecodeError.MissingField; }
        }
        return IpcDecodeError.None;
    }
}

/// <summary>An envelope's payload exceeds even the reassembled limit (4 MiB); nothing was written.</summary>
public sealed class IpcPayloadTooLargeException(string message) : InvalidOperationException(message);

/// <summary>
/// Splits an envelope whose frame would exceed 1 MiB into Base64 parts (PLAN 4.5.4 item 4). A payload
/// whose UTF-8 JSON exceeds 4 MiB is refused up front with <see cref="IpcPayloadTooLargeException"/>,
/// so an oversized result is never silently dropped. Reassembly is <see cref="IpcReassembler"/>.
/// </summary>
public static class IpcFraming
{
    /// <summary>Raw payload bytes per part; Base64 (4/3) plus the envelope stays well under 1 MiB.</summary>
    public const int PartBytes = 512 * 1024;
    public const int MaxParts = (ProtocolLimits.MaxReassembledJsonBytes + PartBytes - 1) / PartBytes;

    /// <summary>Returns the frames to write for <paramref name="envelope"/> (one frame when it fits).</summary>
    public static List<byte[]> Split(IpcEnvelope envelope, Func<long> nextSequence)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(envelope, ContractsJson.Default.IpcEnvelope);
        if (body.Length <= ProtocolLimits.MaxFrameBytes) return [IpcCodec.Encode(envelope with { Sequence = nextSequence() })];
        if (envelope.Payload is not { } payload) throw new IpcPayloadTooLargeException("IPC frame exceeds 1 MiB and has no payload to split.");
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(payload, ContractsJson.Default.JsonElement);
        if (raw.Length > ProtocolLimits.MaxReassembledJsonBytes)
            throw new IpcPayloadTooLargeException($"IPC payload of {raw.Length} bytes exceeds the {ProtocolLimits.MaxReassembledJsonBytes}-byte transfer limit.");
        string transferId = Guid.NewGuid().ToString("N");
        int count = (raw.Length + PartBytes - 1) / PartBytes;
        var frames = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int offset = i * PartBytes;
            string slice = Convert.ToBase64String(raw, offset, Math.Min(PartBytes, raw.Length - offset));
            frames.Add(IpcCodec.Encode(envelope with
            {
                Sequence = nextSequence(),
                Part = new IpcFramePart(transferId, i, count),
                Payload = JsonSerializer.SerializeToElement(slice, ContractsJson.Default.String),
            }));
        }
        return frames;
    }
}

/// <summary>
/// Reassembles <see cref="IpcFramePart"/> envelopes (one instance per reader thread). Parts of one
/// transfer must arrive in order with the same identity; a protocol violation or a transfer over 4 MiB
/// throws <see cref="InvalidDataException"/> (the connection is then treated as broken, like any other
/// malformed frame). An incomplete transfer older than <see cref="StaleAfter"/> is discarded
/// (PLAN 4.5.4: a missing part or timeout cancels the transfer).
/// </summary>
public sealed class IpcReassembler
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    private const int MaxOpenTransfers = 8;
    private readonly Dictionary<string, Transfer> transfers = new(StringComparer.Ordinal);

    private sealed class Transfer(IpcEnvelope first)
    {
        public IpcEnvelope First { get; } = first;
        public MemoryStream Buffer { get; } = new();
        public int Next { get; set; }
        public DateTime Started { get; } = DateTime.UtcNow;
    }

    /// <summary>Open (incomplete) transfers, for diagnostics/tests.</summary>
    public int Open => transfers.Count;

    /// <summary>Returns the envelope to dispatch: the input itself when it is not a part, the whole
    /// envelope when this part completes a transfer, otherwise null.</summary>
    public IpcEnvelope? Accept(IpcEnvelope envelope)
    {
        if (envelope.Part is not { } part) return envelope;
        var now = DateTime.UtcNow;
        foreach (var stale in transfers.Where(t => now - t.Value.Started > StaleAfter).Select(t => t.Key).ToArray()) transfers.Remove(stale);
        Transfer? transfer;
        if (part.Index == 0)
        {
            if (transfers.ContainsKey(part.TransferId)) throw new InvalidDataException("Duplicate IPC transfer id.");
            if (transfers.Count >= MaxOpenTransfers) throw new InvalidDataException("Too many open IPC transfers.");
            transfers[part.TransferId] = transfer = new Transfer(envelope);
        }
        else if (!transfers.TryGetValue(part.TransferId, out transfer)) throw new InvalidDataException("IPC part for an unknown or expired transfer.");
        var first = transfer.First;
        if (part.Index != transfer.Next || part.Count != first.Part!.Count || envelope.Type != first.Type || envelope.RequestId != first.RequestId
            || envelope.JobId != first.JobId || envelope.PluginId != first.PluginId || envelope.Grant != first.Grant)
        { transfers.Remove(part.TransferId); throw new InvalidDataException("IPC part out of order or with a different identity."); }
        byte[] slice;
        try { slice = Convert.FromBase64String(envelope.Payload!.Value.GetString()!); }
        catch (FormatException) { transfers.Remove(part.TransferId); throw new InvalidDataException("IPC part is not Base64."); }
        if (transfer.Buffer.Length + slice.Length > ProtocolLimits.MaxReassembledJsonBytes)
        { transfers.Remove(part.TransferId); throw new InvalidDataException("Reassembled IPC payload exceeds 4 MiB."); }
        transfer.Buffer.Write(slice);
        transfer.Next++;
        if (transfer.Next < part.Count) return null;
        transfers.Remove(part.TransferId);
        JsonElement payload;
        try { using var document = JsonDocument.Parse(transfer.Buffer.ToArray()); payload = document.RootElement.Clone(); }
        catch (JsonException) { throw new InvalidDataException("Reassembled IPC payload is not JSON."); }
        return first with { Part = null, Payload = payload, Sequence = envelope.Sequence };
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

    /// <summary>Writes <paramref name="envelope"/>, split into parts when it exceeds one frame; all parts
    /// are written under <paramref name="gate"/> with consecutive sequence numbers. Throws
    /// <see cref="IpcPayloadTooLargeException"/> (before writing anything) over the 4 MiB transfer limit.</summary>
    public static void WriteFramed(Stream stream, IpcEnvelope envelope, object gate, Func<long> nextSequence)
    {
        lock (gate)
        {
            foreach (byte[] frame in IpcFraming.Split(envelope, nextSequence)) stream.Write(frame);
            stream.Flush();
        }
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
/// <summary><paramref name="RetryAfterRaw"/> is the vendor's own Retry-After header value (delta-seconds
/// or an HTTP-date), passed through unparsed: only the host (RetryPolicy.ParseRetryAfter) decides what
/// it means and whether to honor it (ARCHITECTURE 5.1: the plugin/HTTP layer never retries on its own).</summary>
public sealed record PluginErrorInfo(string Kind, string? Detail, string? RetryAfterRaw = null);
/// <summary>Main → child: cancel one in-flight call; host-side cancellation is authoritative.</summary>
public sealed record CancelPayload(int CallId);
/// <summary>Child → main: a runtime was rebuilt after its execution budget was exceeded.</summary>
public sealed record FaultPayload(string Reason, int[] FailedCalls, double RebuildMilliseconds);
/// <summary>Child → main: aggregate engine stats, polled for diagnostics.</summary>
public sealed record StatsPayload(long EngineBytes, int Runtimes);
/// <summary>Child → main: a plugin console.* message, rate-limited by the host (PLAN 4.5.4).</summary>
public sealed record PluginLogPayload(string Level, string Text);
