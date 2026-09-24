using Susu.Contracts;

namespace Susu.Runtime;

/// <summary>
/// Splits one large plugin/API result into bounded <see cref="IpcMessageType.Chunk"/> frames with a
/// credit-based ACK window (F04.3, ARCHITECTURE 6/PLAN 4.5.1/4.5.4). Each chunk is at most
/// <see cref="ProtocolLimits.MaxStreamChunkBytes"/>; the sender may have at most
/// <see cref="ProtocolLimits.MaxUnackedStreamBytesPerCall"/> bytes of that one call, and at most
/// <see cref="ProtocolLimits.MaxUnackedStreamBytesPerProcess"/> bytes across every call in this
/// process, unacknowledged at a time. This is the reusable flow-control primitive; it does not by
/// itself decide which capability results stream (the current QuickJS bridge - native/quickjs-bridge -
/// only reports a single terminal result per call, so no production capability streams chunks yet;
/// a capability that does would drive this exactly the way large host API responses would).
/// </summary>
public sealed class StreamWindow
{
    private readonly object gate = new();
    private long processUnacked;
    private readonly Dictionary<string, long> perCall = new(StringComparer.Ordinal);

    /// <summary>Splits <paramref name="text"/> into ordered UTF-8 chunk payloads, each within the frame chunk cap.</summary>
    public static IReadOnlyList<ChunkPayload> Split(string attemptId, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var chunks = new List<ChunkPayload>();
        // Splitting on UTF-8 byte boundaries could cut a multi-byte codepoint; slice by chars conservatively
        // (worst case 4 bytes/char) so every chunk's re-encoded size stays within the cap.
        int maxChars = ProtocolLimits.MaxStreamChunkBytes / 4;
        long index = 0;
        for (int i = 0; i < text.Length; i += maxChars)
        {
            string piece = text.Substring(i, Math.Min(maxChars, text.Length - i));
            chunks.Add(new ChunkPayload(attemptId, index++, piece));
        }
        if (chunks.Count == 0) chunks.Add(new ChunkPayload(attemptId, 0, ""));
        return chunks;
    }

    /// <summary>
    /// True when <paramref name="bytes"/> more bytes may be sent for <paramref name="callId"/> without
    /// exceeding either the per-call or per-process unacked budget; if so, the bytes are reserved.
    /// </summary>
    public bool TryReserve(string callId, int bytes)
    {
        lock (gate)
        {
            long current = perCall.GetValueOrDefault(callId);
            if (current + bytes > ProtocolLimits.MaxUnackedStreamBytesPerCall) return false;
            if (processUnacked + bytes > ProtocolLimits.MaxUnackedStreamBytesPerProcess) return false;
            perCall[callId] = current + bytes;
            processUnacked += bytes;
            return true;
        }
    }

    /// <summary>Releases <paramref name="bytes"/> of credit for <paramref name="callId"/> on ACK.</summary>
    public void Ack(string callId, int bytes)
    {
        lock (gate)
        {
            long remaining = Math.Max(0, perCall.GetValueOrDefault(callId) - bytes);
            if (remaining == 0) perCall.Remove(callId); else perCall[callId] = remaining;
            processUnacked = Math.Max(0, processUnacked - bytes);
        }
    }

    /// <summary>Drops every reservation for one call (cancel/completion): its window closes immediately.</summary>
    public void Reset(string callId)
    {
        lock (gate)
        {
            if (perCall.Remove(callId, out long bytes)) processUnacked = Math.Max(0, processUnacked - bytes);
        }
    }

    public long UnackedForCall(string callId) { lock (gate) return perCall.GetValueOrDefault(callId); }
    public long UnackedForProcess { get { lock (gate) return processUnacked; } }
}
