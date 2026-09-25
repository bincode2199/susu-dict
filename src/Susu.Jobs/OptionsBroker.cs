using Susu.Abstractions;
using Susu.Contracts;

namespace Susu.Jobs;

/// <summary>
/// One page request for a dynamic settings field (ARCHITECTURE 3.1). Revision is the field's dependency
/// revision the caller saw (<see cref="OptionsBroker.RevisionOf"/>); Method is <c>options</c> or <c>voices</c>.
/// </summary>
public sealed record OptionsQuery(string InstanceId, string Field, string Method, long Revision, string? Cursor = null);

/// <summary>What a loader (the plugin call) produced: items and the next cursor, or an error kind only.</summary>
public sealed record OptionsLoad(IReadOnlyList<OptionItem>? Items, string? NextCursor = null, ErrorKind? Error = null);

public abstract record OptionsOutcome
{
    public sealed record Loaded(IReadOnlyList<OptionItem> Items, string? NextCursor, bool Cached) : OptionsOutcome;
    /// <summary>Only the classification: vendor detail text (which could echo a request) never reaches a page.</summary>
    public sealed record Failed(ErrorKind Kind) : OptionsOutcome;
    /// <summary>The field's dependencies changed while (or before) the load ran: the result is dropped, not cached (CFG02).</summary>
    public sealed record Stale(long CurrentRevision) : OptionsOutcome;
}

/// <summary>
/// Dynamic option loading for settings fields (F07.2, ARCHITECTURE 3.1, CFG02): one page per call, at most
/// <see cref="MaxItems"/> items, <see cref="Timeout"/> per call (cancelled through the loader's token),
/// results cached for <see cref="CacheLifetime"/> per instance, field, dependency revision and cursor.
///
/// Each (instance, field) has a dependency revision. <see cref="Track"/> bumps it when the fingerprint of
/// the field's dependencies (config values, account binding, origin) changes and <see cref="Invalidate"/>
/// bumps every field of an instance (a credential was written or removed). A bump drops the field's cache,
/// and a load that started under an older revision returns <see cref="OptionsOutcome.Stale"/>: an old
/// response never overwrites what the newer configuration shows.
/// </summary>
public sealed class OptionsBroker(Func<OptionsQuery, CancellationToken, Task<OptionsLoad>> loader, IClock clock)
{
    public const int MaxItems = 200;
    public const int MaxValueLength = 512;
    public const int MaxCursorLength = 2048;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private sealed record Entry(long ExpiresAt, IReadOnlyList<OptionItem> Items, string? NextCursor);

    private readonly object gate = new();
    private readonly Dictionary<(string Instance, string Field), (long Revision, string? Fingerprint)> revisions = [];
    private readonly Dictionary<(string Instance, string Field, long Revision, string Cursor), Entry> cache = [];

    public long RevisionOf(string instanceId, string field)
    {
        lock (gate) return revisions.TryGetValue((instanceId, field), out var r) ? r.Revision : 1;
    }

    /// <summary>Records the current dependency fingerprint and returns the field's revision (bumped if it changed).</summary>
    public long Track(string instanceId, string field, string fingerprint)
    {
        lock (gate)
        {
            var key = (instanceId, field);
            if (!revisions.TryGetValue(key, out var current)) { revisions[key] = (1, fingerprint); return 1; }
            if (current.Fingerprint == fingerprint) return current.Revision;
            Bump(key, current.Revision, fingerprint);
            return current.Revision + 1;
        }
    }

    /// <summary>Every field of <paramref name="instanceId"/> becomes stale (credentials changed).</summary>
    public void Invalidate(string instanceId)
    {
        lock (gate)
            foreach (var key in revisions.Keys.Where(k => k.Instance == instanceId).ToList())
                Bump(key, revisions[key].Revision, revisions[key].Fingerprint);
    }

    private void Bump((string Instance, string Field) key, long revision, string? fingerprint)
    {
        revisions[key] = (revision + 1, fingerprint);
        foreach (var stale in cache.Keys.Where(k => k.Instance == key.Instance && k.Field == key.Field).ToList()) cache.Remove(stale);
    }

    public async Task<OptionsOutcome> LoadAsync(OptionsQuery query, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var cacheKey = (query.InstanceId, query.Field, query.Revision, query.Cursor ?? "");
        lock (gate)
        {
            long current = RevisionOfLocked(query.InstanceId, query.Field);
            if (query.Revision != current) return new OptionsOutcome.Stale(current);
            if (!refresh && cache.TryGetValue(cacheKey, out var hit))
            {
                if (hit.ExpiresAt > clock.NowMilliseconds) return new OptionsOutcome.Loaded(hit.Items, hit.NextCursor, true);
                cache.Remove(cacheKey);
            }
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        OptionsLoad result;
        try
        {
            var load = loader(query, linked.Token);
            var timer = clock.Delay(Timeout, linked.Token);
            if (await Task.WhenAny(load, timer).ConfigureAwait(false) != load)
            {
                cancellationToken.ThrowIfCancellationRequested();
                linked.Cancel(); // the loader sends the plugin its Cancel
                return Settle(query, new OptionsOutcome.Failed(ErrorKind.Timeout));
            }
            result = await load.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Settle(query, new OptionsOutcome.Failed(ErrorKind.Unavailable)); }
        finally { linked.Cancel(); }

        if (result.Error is { } error) return Settle(query, new OptionsOutcome.Failed(error));
        if (Validate(query, result) is { } problem) return Settle(query, problem);
        lock (gate)
        {
            long current = RevisionOfLocked(query.InstanceId, query.Field);
            if (current != query.Revision) return new OptionsOutcome.Stale(current);
            cache[cacheKey] = new Entry(clock.NowMilliseconds + (long)CacheLifetime.TotalMilliseconds, result.Items!, result.NextCursor);
        }
        return new OptionsOutcome.Loaded(result.Items!, result.NextCursor, false);
    }

    /// <summary>A failure for a revision that is no longer current is reported as stale too: it says nothing about the new settings.</summary>
    private OptionsOutcome Settle(OptionsQuery query, OptionsOutcome outcome)
    {
        lock (gate)
        {
            long current = RevisionOfLocked(query.InstanceId, query.Field);
            return current != query.Revision ? new OptionsOutcome.Stale(current) : outcome;
        }
    }

    private long RevisionOfLocked(string instanceId, string field) => revisions.TryGetValue((instanceId, field), out var r) ? r.Revision : 1;

    /// <summary>Host-side output checks (ARCHITECTURE 3.1): scalar value/label pairs only, bounded page, a cursor that moves.</summary>
    private static OptionsOutcome? Validate(OptionsQuery query, OptionsLoad result)
    {
        if (result.Items is null) return new OptionsOutcome.Failed(ErrorKind.BadResponse);
        if (result.Items.Count > MaxItems) return new OptionsOutcome.Failed(ErrorKind.BadResponse);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in result.Items)
        {
            if (item is null || string.IsNullOrEmpty(item.Value) || item.Value.Length > MaxValueLength || item.Value.Any(char.IsControl)) return new OptionsOutcome.Failed(ErrorKind.BadResponse);
            if (item.Label is null || item.Label.Length > MaxValueLength || item.Label.Any(char.IsControl)) return new OptionsOutcome.Failed(ErrorKind.BadResponse);
            if (!seen.Add(item.Value)) return new OptionsOutcome.Failed(ErrorKind.BadResponse);
        }
        if (result.NextCursor is { } next && (next.Length is 0 or > MaxCursorLength || next == query.Cursor)) return new OptionsOutcome.Failed(ErrorKind.BadResponse);
        return null;
    }
}
