// The mutable in-memory table. One entry per user key — the newest sequence
// wins — kept in ordinal (byte-wise) key order so flush writes SSTables
// already sorted. Deletes are tombstones, not removals: an older SSTable may
// still hold the value, and only the tombstone proves it is gone.
namespace LsmStore;

public sealed record MemEntry(ulong Sequence, EntryType Type, byte[]? Value);

public sealed class MemTable
{
    private readonly SortedDictionary<string, MemEntry> _entries = new(StringComparer.Ordinal);
    private long _approximateBytes;

    public int Count => _entries.Count;

    /// <summary>Approximate in-memory footprint; drives the flush threshold.</summary>
    public long ApproximateBytes => _approximateBytes;

    public void Put(string key, ulong sequence, byte[] value)
    {
        Upsert(key, new MemEntry(sequence, EntryType.Put, value));
    }

    public void Delete(string key, ulong sequence)
    {
        Upsert(key, new MemEntry(sequence, EntryType.Delete, null));
    }

    private void Upsert(string key, MemEntry entry)
    {
        if (_entries.TryGetValue(key, out var old))
            _approximateBytes -= Estimate(old, key);
        _entries[key] = entry;
        _approximateBytes += Estimate(entry, key);
    }

    private static long Estimate(MemEntry entry, string key) =>
        System.Text.Encoding.UTF8.GetByteCount(key) + (entry.Value?.Length ?? 0) + 32;

    public MemEntry? Get(string key) => _entries.TryGetValue(key, out var e) ? e : null;

    /// <summary>Entries in key order, as (internal key, value) pairs for the SSTable writer.</summary>
    public IEnumerable<KeyValuePair<byte[], byte[]>> EntriesInOrder()
    {
        foreach (var (key, entry) in _entries)
        {
            var userKey = System.Text.Encoding.UTF8.GetBytes(key);
            var internalKey = InternalKey.Encode(userKey, entry.Sequence, entry.Type);
            yield return new KeyValuePair<byte[], byte[]>(internalKey, entry.Value ?? Array.Empty<byte>());
        }
    }

    public IEnumerable<KeyValuePair<string, MemEntry>> Scan(string? from, string? to)
    {
        foreach (var (key, entry) in _entries)
        {
            if (from is not null && string.CompareOrdinal(key, from) < 0) continue;
            if (to is not null && string.CompareOrdinal(key, to) >= 0) break;
            yield return new KeyValuePair<string, MemEntry>(key, entry);
        }
    }
}
