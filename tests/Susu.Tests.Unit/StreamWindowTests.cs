using Susu.Contracts;
using Susu.Runtime;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>F04.3: bounded stream chunks and a credit-based ACK window.</summary>
public class StreamWindowTests
{
    [Fact]
    public void Splits_text_into_chunks_within_the_frame_cap()
    {
        string text = new string('x', ProtocolLimits.MaxStreamChunkBytes * 2 + 100);
        var chunks = StreamWindow.Split("attempt-1", text);
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(System.Text.Encoding.UTF8.GetByteCount(c.Text) <= ProtocolLimits.MaxStreamChunkBytes));
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Text)));
        Assert.Equal(Enumerable.Range(0, chunks.Count).Select(i => (long)i), chunks.Select(c => c.Index));
    }

    [Fact]
    public void Empty_text_still_produces_one_chunk()
    {
        var chunks = StreamWindow.Split("a", "");
        Assert.Single(chunks);
        Assert.Equal("", chunks[0].Text);
    }

    [Fact]
    public void Reserve_is_denied_once_the_per_call_window_is_full()
    {
        var window = new StreamWindow();
        Assert.True(window.TryReserve("call-1", ProtocolLimits.MaxUnackedStreamBytesPerCall));
        Assert.False(window.TryReserve("call-1", 1));
        window.Ack("call-1", ProtocolLimits.MaxUnackedStreamBytesPerCall);
        Assert.True(window.TryReserve("call-1", 1));
    }

    [Fact]
    public void Reserve_is_denied_once_the_per_process_window_is_full_even_across_calls()
    {
        var window = new StreamWindow();
        int perCallChunk = ProtocolLimits.MaxUnackedStreamBytesPerCall;
        int calls = (int)(ProtocolLimits.MaxUnackedStreamBytesPerProcess / perCallChunk);
        for (int i = 0; i < calls; i++) Assert.True(window.TryReserve($"call-{i}", perCallChunk));
        Assert.False(window.TryReserve("call-overflow", 1));
    }

    [Fact]
    public void Reset_releases_a_calls_reservation_immediately()
    {
        var window = new StreamWindow();
        window.TryReserve("call-1", 1000);
        Assert.Equal(1000, window.UnackedForProcess);
        window.Reset("call-1");
        Assert.Equal(0, window.UnackedForProcess);
        Assert.Equal(0, window.UnackedForCall("call-1"));
    }
}
