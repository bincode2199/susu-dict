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

public sealed record HelloPayload(string Role, string HostBuild, string LaunchNonce, int ProcessId);
public sealed record ChunkPayload(string AttemptId, long Index, string Text);
public sealed record AckPayload(string AttemptId, long Index, int Bytes);
