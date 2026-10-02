using Susu.Contracts;

namespace Susu.Abstractions;

public enum VocabAction { Upsert, Lookup }

/// <summary>One call to a vocabulary target (ARCHITECTURE 8.3). The operationId is the outbox row's, stable across retries and restarts.</summary>
public sealed record VocabSyncRequest(string OperationId, VocabAction Action, string EntryId, long EntryRevision, string Word, string Lang, string ContentJson);

/// <summary>
/// What a target call means for the outbox row: <see cref="Applied"/>/<see cref="Found"/> succeed; <see cref="Absent"/> answers a
/// lookup; <see cref="Unknown"/> means a write may or may not have reached the vendor (never resent blindly); <see cref="Retry"/>
/// means nothing was written (a connection that failed before any write, a vendor 429, a stopped plugin host); <see cref="Failure"/>
/// is a vendor or configuration error that retrying will not fix.
/// </summary>
public abstract record VocabSyncOutcome
{
    public sealed record Applied(string? RemoteId) : VocabSyncOutcome;
    public sealed record Found(string? RemoteId) : VocabSyncOutcome;
    public sealed record Absent : VocabSyncOutcome;
    public sealed record Unknown(string? Detail = null) : VocabSyncOutcome;
    public sealed record Retry(ProviderError Error) : VocabSyncOutcome;
    public sealed record Failure(ProviderError Error) : VocabSyncOutcome;
}

/// <summary>A configured vocabulary service instance (P-V01 AnkiConnect, P-V02 Eudic) as the sync worker sees it.</summary>
public interface IVocabSyncTarget
{
    string InstanceId { get; }
    /// <summary>The package declares a reliable lookup (manifest): Uncertain rows are confirmed or back-filled instead of waiting for the user.</summary>
    bool SupportsLookup { get; }
    Task<VocabSyncOutcome> SendAsync(VocabSyncRequest request, CancellationToken cancellationToken);
}
