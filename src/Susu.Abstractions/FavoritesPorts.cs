namespace Susu.Abstractions;

/// <summary>Per-target delivery states (ARCHITECTURE 8.3). Cancelled is added for an unfavorite that stops a not-yet-sent delivery.</summary>
public enum DeliveryState { Pending, Sending, Succeeded, RetryWait, Uncertain, Failed, Cancelled }

/// <summary>A card the user favorites: language, the word as shown, and the content (JSON of the vocab projection).</summary>
public sealed record FavoriteCard(string Lang, string Word, string ContentJson);

/// <summary>Outcome of favoriting: the entry, its revision, and whether it was new / its content changed / it was restored after an unfavorite.</summary>
public sealed record FavoriteResult(string EntryId, long Revision, bool Created, bool ContentChanged, bool Restored, IReadOnlyList<string> QueuedTargets);

/// <summary>Frozen view of one entry; exporters read these and never the live row (F15.2).</summary>
public sealed record VocabEntrySnapshot(string EntryId, string Lang, string DisplayText, string ContentJson, long Revision, bool Deleted);

/// <summary>One per-target outbox row for (entry, entryRevision, target).</summary>
public sealed record VocabDelivery(string EntryId, string TargetInstanceId, long EntryRevision, string OperationId, DeliveryState State, string? RemoteId, int Attempts, long NextAtMs);

/// <summary>
/// Local favorites (ARCHITECTURE 8.2/8.3). Works with no network and no sync target: an empty target list stores the
/// entry only. Entry write and outbox rows commit in one transaction. Unfavorite marks the entry deleted and cancels
/// unsent deliveries; it never deletes anything remotely.
/// </summary>
public interface IFavorites
{
    /// <summary>Idempotent for the same (lang, normalized word, content); a content change bumps the revision and queues new rows.</summary>
    FavoriteResult Favorite(FavoriteCard card, IReadOnlyList<string> targetInstanceIds);
    /// <summary>Marks the entry deleted and cancels Pending/RetryWait deliveries; returns false when it was not a favorite.</summary>
    bool Unfavorite(string lang, string word);
    bool IsFavorite(string lang, string word);
    VocabEntrySnapshot? Get(string entryId);
    IReadOnlyList<VocabEntrySnapshot> List(bool includeDeleted = false);
    IReadOnlyList<VocabDelivery> Deliveries(string entryId);
    /// <summary>Atomically moves the oldest due Pending/RetryWait row of the target to Sending (attempts + 1).</summary>
    VocabDelivery? Claim(string targetInstanceId);
    /// <summary>Records the consumer result for a Sending row; <paramref name="retryAfter"/> applies to RetryWait.</summary>
    bool Complete(string entryId, string targetInstanceId, long entryRevision, DeliveryState outcome, string? remoteId = null, TimeSpan? retryAfter = null);
    /// <summary>F15.3: moves the oldest Uncertain row of the target to Sending so a lookup can confirm or back-fill it (attempts unchanged); a crash in between returns it to Uncertain.</summary>
    VocabDelivery? ClaimUncertain(string targetInstanceId);
    /// <summary>F15.3 manual check: an Uncertain or Failed row becomes Succeeded (the user confirms it is on the remote) or Pending (resend, same operationId, attempts reset).</summary>
    bool Resolve(string entryId, string targetInstanceId, long entryRevision, bool delivered);
    /// <summary>At start-up: Sending rows become Uncertain (never blindly resent). Returns the count.</summary>
    int RecoverInterrupted();
}
