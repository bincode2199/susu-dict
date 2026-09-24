using Susu.Runtime;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>F04.3: control messages (Cancel/Shutdown) overtake queued data work (Invoke/ApiResult/Load).</summary>
public class PriorityWorkQueueTests
{
    [Fact]
    public void A_control_item_is_taken_before_data_queued_earlier()
    {
        var queue = new PriorityWorkQueue();
        var order = new List<string>();
        // Simulate load: several data items already queued before the control item arrives.
        queue.AddData(() => order.Add("data-1"));
        queue.AddData(() => order.Add("data-2"));
        queue.AddData(() => order.Add("data-3"));
        queue.AddControl(() => order.Add("control-1"));

        // Draining one at a time must surface the control item first, ahead of the backlog.
        queue.TakeNext(1000)!();
        Assert.Equal(["control-1"], order);

        queue.TakeNext(1000)!();
        queue.TakeNext(1000)!();
        queue.TakeNext(1000)!();
        Assert.Equal(["control-1", "data-1", "data-2", "data-3"], order);
    }

    [Fact]
    public void A_later_control_item_still_cuts_the_data_backlog()
    {
        var queue = new PriorityWorkQueue();
        var order = new List<string>();
        for (int i = 0; i < 5; i++) { int captured = i; queue.AddData(() => order.Add($"data-{captured}")); }
        // Control arrives after every data item is already queued - it must still run before
        // any *remaining* queued data (F04.3: control overtakes data under load).
        queue.AddControl(() => order.Add("control"));

        queue.TakeNext(1000)!();
        Assert.Equal("control", order[0]);
    }

    [Fact]
    public void Returns_null_on_timeout_when_nothing_is_queued()
    {
        var queue = new PriorityWorkQueue();
        Assert.Null(queue.TakeNext(50));
    }

    [Fact]
    public void Drain_remaining_returns_control_before_data()
    {
        var queue = new PriorityWorkQueue();
        var order = new List<string>();
        queue.AddData(() => order.Add("d"));
        queue.AddControl(() => order.Add("c"));
        queue.CompleteAdding();
        foreach (var action in queue.DrainRemaining()) action();
        Assert.Equal(["c", "d"], order);
    }
}
