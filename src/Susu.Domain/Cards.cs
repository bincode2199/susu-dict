using Susu.Contracts;

namespace Susu.Domain;

/// <summary>Immutable state of one service card in a translation session (ARCHITECTURE 5).</summary>
public sealed record Card(
    string ServiceId,
    CardState State,
    bool Collapsed,
    string Text,
    long Generation,
    string? AttemptId,
    long LastSequence,
    ErrorKind? Error,
    int Attempts)
{
    public static Card Create(string serviceId, bool collapsed) => new(serviceId, CardState.CollapsedIdle, collapsed, "", 0, null, 0, null, 0);
    public bool IsActive => State is CardState.Queued or CardState.Loading or CardState.Streaming;
}

public abstract record CardEvent
{
    /// <summary>New source text / language / retry-from-scratch: new generation for every card.</summary>
    public sealed record NewGeneration(long Generation, string AttemptId) : CardEvent;
    public sealed record Expand(string AttemptId) : CardEvent;
    public sealed record Collapse : CardEvent;
    public sealed record ManualRetry(string AttemptId) : CardEvent;
    public sealed record Started(EventStamp Stamp) : CardEvent;
    public sealed record Chunk(EventStamp Stamp, string Text) : CardEvent;
    /// <summary>Automatic regeneration after a partial stream: discard the old attempt's text first.</summary>
    public sealed record Reset(EventStamp Stamp) : CardEvent;
    public sealed record Completed(EventStamp Stamp, string Text) : CardEvent;
    public sealed record Failed(EventStamp Stamp, ErrorKind Kind) : CardEvent;
    public sealed record Unsupported : CardEvent;
}

public abstract record CardEffect
{
    public sealed record Request(string ServiceId, long Generation, string AttemptId) : CardEffect;
    public sealed record Cancel(string ServiceId, string AttemptId) : CardEffect;
}

/// <summary>
/// Service-level network state (ARCHITECTURE 13, UI04): one service failing proves nothing about the
/// others, so each card carries its own error, and the shared "offline" notice appears only when every
/// card that requested in the current generation has settled on a retryable network failure.
/// </summary>
public static class NetworkState
{
    public static bool IsNetworkFailure(ErrorKind? kind) => kind is ErrorKind.Network or ErrorKind.Timeout;

    public static bool IsOffline(IEnumerable<Card> cards)
    {
        var requested = cards.Where(c => c.Generation > 0 && c.Attempts > 0 && c.State is not (CardState.CollapsedIdle or CardState.Cancelled or CardState.Unsupported)).ToList();
        return requested.Count > 0 && requested.All(c => c.State == CardState.Failed && IsNetworkFailure(c.Error));
    }
}

/// <summary>
/// Pure card reducer. Only the current generation and attempt may change a card; sequences are strictly
/// increasing; a terminal result is applied once. The first expand creates the request; collapsing cancels
/// unfinished work and clears partial text; a completed result is reused on re-expand within its generation.
/// </summary>
public static class CardReducer
{
    public static (Card Card, IReadOnlyList<CardEffect> Effects) Reduce(Card card, CardEvent @event)
    {
        switch (@event)
        {
            case CardEvent.NewGeneration g:
            {
                var effects = new List<CardEffect>();
                if (card.IsActive && card.AttemptId is not null) effects.Add(new CardEffect.Cancel(card.ServiceId, card.AttemptId));
                if (card.State == CardState.Unsupported) return (card with { Generation = g.Generation, Text = "", AttemptId = null, LastSequence = 0 }, effects);
                if (card.Collapsed) return (card with { State = CardState.CollapsedIdle, Text = "", Generation = g.Generation, AttemptId = null, LastSequence = 0, Error = null, Attempts = 0 }, effects);
                effects.Add(new CardEffect.Request(card.ServiceId, g.Generation, g.AttemptId));
                return (card with { State = CardState.Queued, Text = "", Generation = g.Generation, AttemptId = g.AttemptId, LastSequence = 0, Error = null, Attempts = 1 }, effects);
            }
            case CardEvent.Expand e:
            {
                if (!card.Collapsed) return (card, []);
                var expanded = card with { Collapsed = false };
                // Reuse a completed result or keep a failure/unsupported state; otherwise request now.
                if (card.State is CardState.Ready or CardState.Failed or CardState.Unsupported || card.Generation == 0) return (expanded, []);
                return (expanded with { State = CardState.Queued, Text = "", AttemptId = e.AttemptId, LastSequence = 0, Error = null, Attempts = card.Attempts + 1 },
                    [new CardEffect.Request(card.ServiceId, card.Generation, e.AttemptId)]);
            }
            case CardEvent.Collapse:
            {
                if (card.Collapsed) return (card, []);
                if (!card.IsActive) return (card with { Collapsed = true }, []);
                var effects = card.AttemptId is null ? [] : new CardEffect[] { new CardEffect.Cancel(card.ServiceId, card.AttemptId) };
                return (card with { Collapsed = true, State = CardState.Cancelled, Text = "", AttemptId = null }, effects);
            }
            case CardEvent.ManualRetry r:
            {
                if (card.Collapsed || card.State is CardState.Unsupported || card.Generation == 0) return (card, []);
                var effects = new List<CardEffect>();
                if (card.IsActive && card.AttemptId is not null) effects.Add(new CardEffect.Cancel(card.ServiceId, card.AttemptId));
                effects.Add(new CardEffect.Request(card.ServiceId, card.Generation, r.AttemptId));
                // A manual retry is a new operation: clear the old stream first.
                return (card with { State = CardState.Queued, Text = "", AttemptId = r.AttemptId, LastSequence = 0, Error = null, Attempts = card.Attempts + 1 }, effects);
            }
            case CardEvent.Unsupported:
                return (card with { State = CardState.Unsupported, Text = "", AttemptId = null }, []);
        }
        // Remaining events are provider results: filter by generation, attempt and sequence.
        var stamp = @event switch
        {
            CardEvent.Started s => s.Stamp, CardEvent.Chunk c => c.Stamp, CardEvent.Reset r => r.Stamp,
            CardEvent.Completed c => c.Stamp, CardEvent.Failed f => f.Stamp, _ => throw new ArgumentOutOfRangeException(nameof(@event)),
        };
        if (stamp.Generation != card.Generation || !card.IsActive) return (card, []); // stale or already terminal
        if (@event is CardEvent.Reset reset)
        {
            // New attempt of the same card; drop everything of the previous attempt.
            return (card with { State = CardState.Loading, Text = "", AttemptId = reset.Stamp.AttemptId, LastSequence = 0, Attempts = card.Attempts + 1 }, []);
        }
        if (!string.Equals(stamp.AttemptId, card.AttemptId, StringComparison.Ordinal)) return (card, []);
        if (stamp.Sequence <= card.LastSequence) return (card, []); // duplicate or out of order
        return @event switch
        {
            CardEvent.Started => (card with { State = CardState.Loading, LastSequence = stamp.Sequence }, []),
            CardEvent.Chunk c => (card with { State = CardState.Streaming, Text = card.Text + c.Text, LastSequence = stamp.Sequence }, []),
            CardEvent.Completed c => (card with { State = CardState.Ready, Text = c.Text, LastSequence = stamp.Sequence, Error = null }, []),
            CardEvent.Failed f when f.Kind == ErrorKind.Cancelled => (card with { State = CardState.Cancelled, Text = "", LastSequence = stamp.Sequence }, []),
            CardEvent.Failed f when f.Kind == ErrorKind.UnsupportedLanguage => (card with { State = CardState.Unsupported, Collapsed = true, Text = "", LastSequence = stamp.Sequence, Error = f.Kind }, []),
            CardEvent.Failed f => (card with { State = CardState.Failed, Text = "", LastSequence = stamp.Sequence, Error = f.Kind }, []),
            _ => (card, []),
        };
    }
}
