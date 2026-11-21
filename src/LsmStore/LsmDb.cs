// The public database: a memtable + WAL for writes, immutable SSTables for
// durable reads, and size-tiered compaction. Thread safety comes from a
// single ReaderWriterLockSlim: many concurrent readers, one writer. Reads
// consult the memtable first, then SSTables newest-first; the first version
// found wins, and a tombstone means "deleted" even if an older table still
// holds the value.
namespace LsmStore;

using System.Text;

public sealed record DbInfo(
    string Directory,
    int MemTableEntries,
    long MemTableBytes,
    int TableCount,
    long TotalTableBytes,
    long TotalEntries,
    ulong NextSequence);

public sealed class LsmDb : IDisposable
{
    private readonly string _dir;
    private readonly LsmOptions _options;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly List<SSTableReader> _tables = new(); // newest first
    private readonly Manifest _manifest;
    private MemTable _memtable = new();
    private WalWriter _wal;
    private ulong _nextSeq;
    private bool _disposed;

    public string Directory => _dir;

    private LsmDb(string dir, LsmOptions options, Manifest manifest)
    {
        _dir = dir;
        _options = options;
        _manifest = manifest;
        _wal = null!; // assigned by Open after WAL replay
    }

    public static LsmDb Open(string directory, LsmOptions? options = null)
    {
        options ??= new LsmOptions();
        System.IO.Directory.CreateDirectory(directory);

        var manifestPath = Path.Combine(directory, "MANIFEST");
        var manifest = Manifest.Load(manifestPath) ?? new Manifest();
        var db = new LsmDb(directory, options, manifest);

        foreach (var table in manifest.Tables)
            db._tables.Add(SSTableReader.Open(Path.Combine(directory, table.FileName)));

        // Replay the WAL into a fresh memtable; a torn tail is truncated.
        var walPath = Path.Combine(directory, manifest.CurrentWal);
        ulong maxWalSeq = 0;
        if (File.Exists(walPath))
        {
            var (records, maxSeq, validBytes) = WalReader.Replay(walPath);
            maxWalSeq = maxSeq;
            foreach (var record in records)
            {
                var key = Encoding.UTF8.GetString(record.Key);
                if (record.Type == EntryType.Put)
                    db._memtable.Put(key, record.Sequence, record.Value);
                else
                    db._memtable.Delete(key, record.Sequence);
            }
            if (validBytes < new FileInfo(walPath).Length)
                WalReader.TruncateTo(walPath, validBytes);
        }

        db._nextSeq = Math.Max(manifest.NextSequence, maxWalSeq + 1);
        db._wal = new WalWriter(walPath, append: true);
        return db;
    }

    public void Put(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        _lock.EnterWriteLock();
        try
        {
            var seq = _nextSeq++;
            _wal.Append(EntryType.Put, seq, keyBytes, valueBytes);
            _memtable.Put(key, seq, valueBytes);
            if (_memtable.ApproximateBytes >= _options.MemTableBytes)
            {
                FlushLocked();
                if (_tables.Count >= _options.CompactionThreshold)
                    MergeAllLocked();
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>Deletes a key. Returns true when the key existed.</summary>
    public bool Delete(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _lock.EnterWriteLock();
        try
        {
            var existed = GetLocked(key) is not null;
            var seq = _nextSeq++;
            _wal.Append(EntryType.Delete, seq, Encoding.UTF8.GetBytes(key), Array.Empty<byte>());
            _memtable.Delete(key, seq);
            if (_memtable.ApproximateBytes >= _options.MemTableBytes)
            {
                FlushLocked();
                if (_tables.Count >= _options.CompactionThreshold)
                    MergeAllLocked();
            }
            return existed;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public string? Get(string key)
    {
        _lock.EnterReadLock();
        try { return GetLocked(key); }
        finally { _lock.ExitReadLock(); }
    }

    private string? GetLocked(string key)
    {
        var entry = _memtable.Get(key);
        if (entry is not null)
            return entry.Type == EntryType.Put ? Encoding.UTF8.GetString(entry.Value!) : null;

        var userKey = Encoding.UTF8.GetBytes(key);
        foreach (var table in _tables)
        {
            var hit = table.GetUserKey(userKey);
            if (hit is not null)
                return hit.Value.Type == EntryType.Put
                    ? Encoding.UTF8.GetString(hit.Value.Value)
                    : null;
        }
        return null;
    }

    /// <summary>Ordered scan. `from` is inclusive, `to` exclusive; `prefix`
    /// filters keys. All bounds are ordinal string comparisons.</summary>
    public IEnumerable<KeyValuePair<string, string>> Scan(string? from = null, string? to = null, string? prefix = null)
    {
        _lock.EnterReadLock();
        try
        {
            // Merge newest-first: first version seen per key wins.
            var merged = new SortedDictionary<string, (ulong Seq, EntryType Type, byte[]? Value)>(StringComparer.Ordinal);
            void Absorb(string key, ulong seq, EntryType type, byte[]? value)
            {
                if (merged.TryGetValue(key, out var current) && current.Seq >= seq) return;
                merged[key] = (seq, type, value);
            }

            foreach (var (key, entry) in _memtable.Scan(null, null))
                Absorb(key, entry.Sequence, entry.Type, entry.Value);
            foreach (var table in _tables)
            {
                foreach (var (internalKey, value) in table.Entries())
                {
                    var (userKey, seq, type) = InternalKey.Decode(internalKey);
                    Absorb(Encoding.UTF8.GetString(userKey), seq, type, value);
                }
            }

            var result = new List<KeyValuePair<string, string>>();
            foreach (var (key, entry) in merged)
            {
                if (entry.Type == EntryType.Delete) continue;
                if (from is not null && string.CompareOrdinal(key, from) < 0) continue;
                if (to is not null && string.CompareOrdinal(key, to) >= 0) continue;
                if (prefix is not null && !key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                result.Add(new KeyValuePair<string, string>(key, Encoding.UTF8.GetString(entry.Value!)));
            }
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Write the memtable to a new SSTable and rotate the WAL.</summary>
    public void Flush()
    {
        _lock.EnterWriteLock();
        try { FlushLocked(); }
        finally { _lock.ExitWriteLock(); }
    }

    private void FlushLocked()
    {
        if (_memtable.Count == 0) return;

        var fileNumber = _manifest.NextFileNumber++;
        var fileName = $"{fileNumber:D6}.sst";
        var path = Path.Combine(_dir, fileName);
        TableInfo info;
        ulong maxSeq = 0;
        using (var writer = new SSTableWriter(path, _options))
        {
            foreach (var (internalKey, value) in _memtable.EntriesInOrder())
            {
                writer.Add(internalKey, value);
                maxSeq = Math.Max(maxSeq, InternalKey.Decode(internalKey).Sequence);
            }
            info = writer.Finish(fileName, maxSeq);
        }
        _tables.Insert(0, SSTableReader.Open(path));
        _manifest.Tables.Insert(0, new ManifestTable(
            info.FileName, info.MinKey, info.MaxKey, info.MaxSequence, info.Entries, info.SizeBytes));

        // Manifest first (it now names the new SSTable and the new WAL), then
        // drop the old WAL: a crash in between only orphans files.
        var newWalNumber = _manifest.NextFileNumber++;
        var oldWal = _manifest.CurrentWal;
        _manifest.CurrentWal = $"{newWalNumber:D6}.log";
        _manifest.NextSequence = _nextSeq;
        _manifest.Save(_dir);

        _wal.Dispose();
        File.Delete(Path.Combine(_dir, oldWal));
        _wal = new WalWriter(Path.Combine(_dir, _manifest.CurrentWal));
        _memtable = new MemTable();
    }

    /// <summary>Merge every SSTable into one, dropping shadowed versions and
    /// tombstones (safe: a full merge sees all live data).</summary>
    public void Compact()
    {
        _lock.EnterWriteLock();
        try
        {
            FlushLocked();
            MergeAllLocked();
        }
        finally { _lock.ExitWriteLock(); }
    }

    private void MergeAllLocked()
    {
        if (_tables.Count < 2) return;

        var all = new List<KeyValuePair<byte[], byte[]>>();
        foreach (var table in _tables)
            all.AddRange(table.Entries());
        all.Sort((a, b) => InternalKey.Compare(a.Key, b.Key));

        var fileNumber = _manifest.NextFileNumber++;
        var fileName = $"{fileNumber:D6}.sst";
        var path = Path.Combine(_dir, fileName);
        TableInfo? info = null;
        ulong maxSeq = 0;
        using (var writer = new SSTableWriter(path, _options))
        {
            string? lastUserKey = null;
            foreach (var (internalKey, value) in all)
            {
                var (userKey, seq, type) = InternalKey.Decode(internalKey);
                var userKeyString = Encoding.UTF8.GetString(userKey);
                if (userKeyString == lastUserKey) continue; // shadowed by a newer version
                lastUserKey = userKeyString;
                if (type == EntryType.Delete) continue; // tombstone: nothing older survives a full merge
                writer.Add(internalKey, value);
                maxSeq = Math.Max(maxSeq, seq);
            }
            if (writer.Entries > 0)
                info = writer.Finish(fileName, maxSeq);
        }
        if (info is null)
            File.Delete(path);

        var oldTables = _tables.ToList();
        _tables.Clear();
        _manifest.Tables.Clear();
        if (info is not null)
        {
            _tables.Add(SSTableReader.Open(path));
            _manifest.Tables.Add(new ManifestTable(
                info.FileName, info.MinKey, info.MaxKey, info.MaxSequence, info.Entries, info.SizeBytes));
        }
        _manifest.NextSequence = _nextSeq;
        _manifest.Save(_dir);

        foreach (var table in oldTables)
        {
            var tablePath = table.Path;
            table.Dispose();
            File.Delete(tablePath);
        }
    }

    public DbInfo Info()
    {
        _lock.EnterReadLock();
        try
        {
            return new DbInfo(
                _dir,
                _memtable.Count,
                _memtable.ApproximateBytes,
                _tables.Count,
                _tables.Sum(t => new FileInfo(t.Path).Length),
                _memtable.Count + _manifest.Tables.Sum(t => t.Entries),
                _nextSeq);
        }
        finally { _lock.ExitReadLock(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock.EnterWriteLock();
        try
        {
            _wal.Dispose();
            foreach (var table in _tables)
                table.Dispose();
            _tables.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }
}
