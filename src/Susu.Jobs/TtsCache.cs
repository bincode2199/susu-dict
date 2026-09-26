using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Susu.Abstractions;
using Susu.Domain;

namespace Susu.Jobs;

/// <summary>Bounds of the TTS audio cache (ARCHITECTURE 8.4 "TTS 磁盘缓存 … 上限 32 MiB、7 天").</summary>
public sealed record TtsCacheLimits(long MaxBytes, TimeSpan MaxAge, int MaxEntries)
{
    /// <summary>32 MiB and 7 days per ARCHITECTURE 8.4; the 256-entry cap only keeps the index small (not in the spec).</summary>
    public static readonly TtsCacheLimits Default = new(32L << 20, TimeSpan.FromDays(7), 256);
}

/// <summary>
/// F10.3 TTS audio cache (ARCHITECTURE 8.4, TEST-PLAN TTS02 "缓存按配置区分"). Replaying the same text with the same service
/// instance, voice, language, speed and configuration reuses the clip instead of synthesizing or downloading it again.
///
/// <para><b>Key.</b> SHA-256 over the instance id, the instance's configuration fingerprint (package, instance revision,
/// every config value, account bindings and a credential epoch bumped by <see cref="Invalidate"/>), voice, language,
/// clamped speed and the text. The text itself is never stored or logged; secret values are never part of the key.</para>
///
/// <para><b>Invalidation.</b> A lookup under a changed configuration drops that instance's older entries; a settings change
/// (<see cref="Retain"/>) drops every entry whose instance configuration changed or disappeared; a credential write or
/// removal (<see cref="Invalidate"/>) drops the instance's entries. <see cref="Clear"/> is the data-cleanup entry.</para>
///
/// <para><b>Storage.</b> Entries are file leases in the session cache folder (F05/F10.1 lease rules): the cache holds its
/// own reference (<see cref="IShareableAudioClip.Share"/>), the player gets another, and the file is deleted when the last
/// one goes. So the cache lives only as long as the app session (the lease store deletes the session folder on exit and
/// stale ones at startup); a persistent cross-session disk cache needs an explicit setting (ARCHITECTURE 8.4 "明确启用缓存时"),
/// which does not exist yet. Within the session the 32 MiB / 7 day / entry bounds apply, least recently used first.</para>
/// </summary>
public sealed class TtsCache(IClock clock, TtsCacheLimits? limits = null) : IDisposable
{
    private sealed class Entry(string instanceId, string fingerprint, IAudioClip clip, long bytes, long created)
    {
        public string InstanceId { get; } = instanceId;
        public string Fingerprint { get; } = fingerprint;
        public IAudioClip Clip { get; } = clip;
        public long Bytes { get; } = bytes;
        public long Created { get; } = created;
        public long LastUsed = created;
    }

    private readonly TtsCacheLimits limits = limits ?? TtsCacheLimits.Default;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> epochs = new(StringComparer.Ordinal);
    private long bytes;
    private bool disposed;

    public TtsCacheLimits Limits => limits;
    public int Count { get { lock (gate) return entries.Count; } }
    public long Bytes { get { lock (gate) return bytes; } }

    /// <summary>
    /// The configuration fingerprint of <paramref name="instanceId"/> (null when the instance is not configured). Any change of
    /// package, revision, config value, account binding or credential epoch changes it.
    /// </summary>
    public string? FingerprintOf(AppSettings settings, string instanceId)
    {
        var instance = settings.Instances.FirstOrDefault(i => i.Id == instanceId);
        long epoch;
        lock (gate) epoch = epochs.GetValueOrDefault(instanceId);
        if (instance is null) return null;
        var b = new StringBuilder();
        b.Append(instance.Package).Append('\u0001').Append(instance.Revision.ToString(CultureInfo.InvariantCulture)).Append('\u0001').Append(epoch.ToString(CultureInfo.InvariantCulture));
        foreach (var kv in instance.Config.OrderBy(k => k.Key, StringComparer.Ordinal)) b.Append('\u0002').Append(kv.Key).Append('=').Append(kv.Value);
        foreach (var kv in instance.AccountBindings.OrderBy(k => k.Key, StringComparer.Ordinal)) b.Append('\u0003').Append(kv.Key).Append("->").Append(kv.Value);
        return Hash(b.ToString());
    }

    /// <summary>The cache key of one request under a configuration fingerprint (hex SHA-256; carries no text).</summary>
    public static string KeyFor(string instanceId, string fingerprint, SpeakRequest request)
        => Hash(string.Join('\u0001', instanceId, fingerprint, request.Voice ?? "", request.Lang ?? "",
            request.ClampedRate.ToString("R", CultureInfo.InvariantCulture), request.Text));

    /// <summary>
    /// A new owner reference on the cached clip for this request, or null (miss, expired, or the configuration changed —
    /// then the instance's entries under older configurations are dropped). The caller disposes what it gets.
    /// </summary>
    public IAudioClip? TryGet(AppSettings settings, string instanceId, SpeakRequest request)
    {
        if (FingerprintOf(settings, instanceId) is not { } fingerprint) return null;
        string key = KeyFor(instanceId, fingerprint, request);
        List<IAudioClip> dropped = [];
        IAudioClip? hit = null;
        lock (gate)
        {
            if (disposed) return null;
            DropLocked(e => e.InstanceId == instanceId && e.Fingerprint != fingerprint, dropped);
            ExpireLocked(dropped);
            if (entries.TryGetValue(key, out var entry))
            {
                hit = (entry.Clip as IShareableAudioClip)?.Share();
                if (hit is null) RemoveLocked(key, entry, dropped); // the file went away underneath: forget it
                else entry.LastUsed = clock.NowMilliseconds;
            }
        }
        Release(dropped);
        return hit;
    }

    /// <summary>
    /// Keeps a reference on <paramref name="clip"/> for this request (the caller keeps its own). Only shareable leased clips no
    /// larger than the byte bound are kept; the least recently used entries go until the bounds hold again.
    /// </summary>
    public void Add(AppSettings settings, string instanceId, SpeakRequest request, IAudioClip clip)
    {
        if (clip is not IShareableAudioClip shareable || FingerprintOf(settings, instanceId) is not { } fingerprint) return;
        long size = clip.Bytes;
        if (size <= 0 || size > limits.MaxBytes || limits.MaxEntries <= 0) return;
        if (shareable.Share() is not { } own) return;
        string key = KeyFor(instanceId, fingerprint, request);
        List<IAudioClip> dropped = [];
        lock (gate)
        {
            if (disposed) { dropped.Add(own); }
            else
            {
                if (entries.TryGetValue(key, out var old)) RemoveLocked(key, old, dropped);
                DropLocked(e => e.InstanceId == instanceId && e.Fingerprint != fingerprint, dropped);
                ExpireLocked(dropped);
                long now = clock.NowMilliseconds;
                entries[key] = new Entry(instanceId, fingerprint, own, size, now);
                bytes += size;
                while ((bytes > limits.MaxBytes || entries.Count > limits.MaxEntries) && entries.Count > 0)
                {
                    var oldest = entries.MinBy(e => e.Value.LastUsed);
                    RemoveLocked(oldest.Key, oldest.Value, dropped);
                }
            }
        }
        Release(dropped);
    }

    /// <summary>Settings changed: drops every entry whose instance configuration changed or whose instance is gone.</summary>
    public void Retain(AppSettings settings)
    {
        var current = new Dictionary<string, string?>(StringComparer.Ordinal);
        List<string> instances;
        lock (gate) instances = [.. entries.Values.Select(e => e.InstanceId).Distinct()];
        foreach (var id in instances) current[id] = FingerprintOf(settings, id);
        List<IAudioClip> dropped = [];
        lock (gate) DropLocked(e => !current.TryGetValue(e.InstanceId, out var f) || f != e.Fingerprint, dropped);
        Release(dropped);
    }

    /// <summary>A credential of <paramref name="instanceId"/> was written or removed: its entries go and later keys differ.</summary>
    public void Invalidate(string instanceId)
    {
        List<IAudioClip> dropped = [];
        lock (gate)
        {
            epochs[instanceId] = epochs.GetValueOrDefault(instanceId) + 1;
            DropLocked(e => e.InstanceId == instanceId, dropped);
        }
        Release(dropped);
    }

    /// <summary>Data cleanup: releases every entry (a file still playing stays until the player lets go).</summary>
    public void Clear()
    {
        List<IAudioClip> dropped = [];
        lock (gate) DropLocked(_ => true, dropped);
        Release(dropped);
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        Clear();
    }

    private void ExpireLocked(List<IAudioClip> dropped)
    {
        long oldest = clock.NowMilliseconds - (long)limits.MaxAge.TotalMilliseconds;
        DropLocked(e => e.Created < oldest, dropped);
    }

    private void DropLocked(Func<Entry, bool> predicate, List<IAudioClip> dropped)
    {
        foreach (var (key, entry) in entries.Where(e => predicate(e.Value)).ToList()) RemoveLocked(key, entry, dropped);
    }

    private void RemoveLocked(string key, Entry entry, List<IAudioClip> dropped)
    {
        if (!entries.Remove(key)) return;
        bytes -= entry.Bytes;
        dropped.Add(entry.Clip);
    }

    private static void Release(List<IAudioClip> dropped)
    {
        foreach (var clip in dropped) clip.Dispose();
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
