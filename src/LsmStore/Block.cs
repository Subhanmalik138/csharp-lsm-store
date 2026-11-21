// Block format (LevelDB-style): a sequence of prefix-compressed entries,
// then a restart-point array, then the restart count — all little-endian.
// Entry: varint shared, varint non-shared, varint value-length, key delta,
// value. Every `restartInterval` entries start a restart point whose key is
// stored in full, so a lookup binary-searches the restart array and then
// scans forward linearly, decoding at most one interval of entries.
namespace LsmStore;

public sealed class BlockBuilder
{
    private readonly List<byte> _buffer = new();
    private readonly List<int> _restarts = new() { 0 };
    private readonly int _restartInterval;
    private int _entriesSinceRestart;
    private byte[] _lastKey = Array.Empty<byte>();

    public BlockBuilder(int restartInterval) => _restartInterval = restartInterval;

    public int EntryCount { get; private set; }
    public int EstimatedSize => _buffer.Count;

    public void Add(byte[] key, byte[] value)
    {
        if (key.Length == 0) throw new ArgumentException("Block keys must not be empty.", nameof(key));

        var shared = 0;
        if (_entriesSinceRestart >= _restartInterval)
        {
            _restarts.Add(_buffer.Count);
            _entriesSinceRestart = 0;
        }
        else
        {
            var n = Math.Min(_lastKey.Length, key.Length);
            while (shared < n && _lastKey[shared] == key[shared]) shared++;
        }

        Span<byte> varint = stackalloc byte[10];
        var w = Varint.WriteUInt32(varint, (uint)shared);
        _buffer.AddRange(varint[..w].ToArray());
        w = Varint.WriteUInt32(varint, (uint)(key.Length - shared));
        _buffer.AddRange(varint[..w].ToArray());
        w = Varint.WriteUInt32(varint, (uint)value.Length);
        _buffer.AddRange(varint[..w].ToArray());
        _buffer.AddRange(key.AsSpan(shared).ToArray());
        _buffer.AddRange(value);

        _lastKey = key.ToArray();
        _entriesSinceRestart++;
        EntryCount++;
    }

    public byte[] Finish()
    {
        foreach (var restart in _restarts)
            _buffer.AddRange(BitConverter.GetBytes(restart));
        _buffer.AddRange(BitConverter.GetBytes(_restarts.Count));
        return _buffer.ToArray();
    }

    public void Reset()
    {
        _buffer.Clear();
        _restarts.Clear();
        _restarts.Add(0);
        _entriesSinceRestart = 0;
        _lastKey = Array.Empty<byte>();
        EntryCount = 0;
    }
}

public sealed class Block
{
    private readonly byte[] _data; // entry bytes only; restart array stripped
    private readonly int[] _restarts;

    private Block(byte[] data, int[] restarts)
    {
        _data = data;
        _restarts = restarts;
    }

    public static Block Parse(byte[] blockBytes)
    {
        if (blockBytes.Length < 4)
            throw new InvalidDataException("Block is too short for its restart count.");
        var numRestarts = BitConverter.ToInt32(blockBytes, blockBytes.Length - 4);
        if (numRestarts < 1 || blockBytes.Length < 4 + 4 * numRestarts)
            throw new InvalidDataException("Block has an invalid restart array.");
        var restarts = new int[numRestarts];
        var restartBase = blockBytes.Length - 4 - 4 * numRestarts;
        for (var i = 0; i < numRestarts; i++)
            restarts[i] = BitConverter.ToInt32(blockBytes, restartBase + 4 * i);
        var data = blockBytes.AsSpan(0, restartBase).ToArray();
        return new Block(data, restarts);
    }

    private (byte[] Key, byte[] Value, int NextOffset) DecodeAt(int offset, byte[] lastKey)
    {
        var p = offset;
        var shared = (int)Varint.ReadUInt32(_data.AsSpan(p), out var n); p += n;
        var nonShared = (int)Varint.ReadUInt32(_data.AsSpan(p), out n); p += n;
        var valueLen = (int)Varint.ReadUInt32(_data.AsSpan(p), out n); p += n;
        if (shared > lastKey.Length || p + nonShared + valueLen > _data.Length)
            throw new InvalidDataException("Block entry overruns its block.");
        var key = new byte[shared + nonShared];
        lastKey.AsSpan(0, shared).CopyTo(key);
        _data.AsSpan(p, nonShared).CopyTo(key.AsSpan(shared));
        p += nonShared;
        var value = _data.AsSpan(p, valueLen).ToArray();
        p += valueLen;
        return (key, value, p);
    }

    private byte[] KeyAtRestart(int restartIndex)
    {
        var offset = _restarts[restartIndex];
        var (key, _, _) = DecodeAt(offset, Array.Empty<byte>());
        return key;
    }

    /// <summary>Exact lookup; returns null when the key is absent.</summary>
    public byte[]? Get(byte[] key, IComparer<byte[]> comparer)
    {
        // Binary-search the restart array for the last restart whose key <= target.
        var lo = 0;
        var hi = _restarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (comparer.Compare(KeyAtRestart(mid), key) <= 0) lo = mid;
            else hi = mid - 1;
        }

        var offset = _restarts[lo];
        var lastKey = Array.Empty<byte>();
        while (offset < _data.Length)
        {
            var (k, v, next) = DecodeAt(offset, lastKey);
            var cmp = comparer.Compare(k, key);
            if (cmp == 0) return v;
            if (cmp > 0) return null;
            lastKey = k;
            offset = next;
        }
        return null;
    }

    /// <summary>First entry whose key is >= <paramref name="key"/>; null when none.</summary>
    public KeyValuePair<byte[], byte[]>? Seek(byte[] key, IComparer<byte[]> comparer)
    {
        var lo = 0;
        var hi = _restarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (comparer.Compare(KeyAtRestart(mid), key) <= 0) lo = mid;
            else hi = mid - 1;
        }

        var offset = _restarts[lo];
        var lastKey = Array.Empty<byte>();
        while (offset < _data.Length)
        {
            var (k, v, next) = DecodeAt(offset, lastKey);
            if (comparer.Compare(k, key) >= 0)
                return new KeyValuePair<byte[], byte[]>(k, v);
            lastKey = k;
            offset = next;
        }
        return null;
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> Entries()
    {
        var offset = 0;
        var lastKey = Array.Empty<byte>();
        while (offset < _data.Length)
        {
            var (key, value, next) = DecodeAt(offset, lastKey);
            yield return new KeyValuePair<byte[], byte[]>(key, value);
            lastKey = key;
            offset = next;
        }
    }
}
