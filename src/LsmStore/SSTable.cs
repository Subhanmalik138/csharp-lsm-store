// SSTable file layout (LevelDB-inspired, simplified):
//   [data block][5-byte trailer] ... [metaindex block][trailer]
//   [index block][trailer] [48-byte footer]
// A block trailer is 1 compression byte (0 = none) + 4-byte CRC32 of the
// block bytes plus the compression byte. The footer holds two block handles
// (metaindex, index), each varint offset + varint size padded to 20 bytes,
// followed by the 8-byte magic "LSMSTORE". The index maps the last internal
// key of each data block to that block's handle; the metaindex carries one
// entry, "lsm.maxseq", so open can recover the highest sequence cheaply.
namespace LsmStore;

using System.Text;

public sealed record BlockHandle(long Offset, long Size)
{
    public byte[] Encode()
    {
        Span<byte> buf = stackalloc byte[20];
        var w = Varint.WriteUInt64(buf, (ulong)Offset);
        w += Varint.WriteUInt64(buf[w..], (ulong)Size);
        return buf[..w].ToArray();
    }

    public static BlockHandle Decode(ReadOnlySpan<byte> source, out int bytesRead)
    {
        var offset = (long)Varint.ReadUInt64(source, out var n);
        var size = (long)Varint.ReadUInt64(source[n..], out var m);
        bytesRead = n + m;
        return new BlockHandle(offset, size);
    }
}

public sealed record TableInfo(
    string FileName,
    string MinKey,
    string MaxKey,
    ulong MaxSequence,
    long Entries,
    long SizeBytes);

public sealed class SSTableWriter : IDisposable
{
    public const string Magic = "LSMSTORE";
    private const int FooterSize = 48;

    private readonly FileStream _out;
    private readonly LsmOptions _options;
    private readonly BlockBuilder _block;
    private readonly BlockBuilder _index;
    private byte[]? _lastKey;
    private byte[]? _minKey;
    private byte[]? _maxKey;
    private long _entries;
    private bool _finished;

    public SSTableWriter(string path, LsmOptions options)
    {
        _options = options;
        _out = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        _block = new BlockBuilder(options.BlockRestartInterval);
        _index = new BlockBuilder(1);
    }

    public long Entries => _entries;

    public void Add(byte[] internalKey, byte[] value)
    {
        if (_finished) throw new InvalidOperationException("Table is already finished.");
        _block.Add(internalKey, value);
        _lastKey = internalKey.ToArray();
        _minKey ??= internalKey.ToArray();
        _maxKey = internalKey.ToArray();
        _entries++;
        if (_block.EstimatedSize >= _options.BlockSizeBytes)
            FlushBlock();
    }

    private void FlushBlock()
    {
        if (_block.EntryCount == 0) return;
        var blockBytes = _block.Finish();
        var offset = _out.Position;
        WriteBlock(blockBytes);
        _index.Add(_lastKey!, new BlockHandle(offset, blockBytes.Length).Encode());
        _block.Reset();
    }

    private void WriteBlock(byte[] blockBytes)
    {
        _out.Write(blockBytes);
        _out.WriteByte(0); // no compression
        Span<byte> typeAndCrc = stackalloc byte[5];
        typeAndCrc[0] = 0;
        var crcInput = new byte[blockBytes.Length + 1];
        blockBytes.CopyTo(crcInput, 0);
        var crc = Crc32.Compute(crcInput);
        BitConverter.TryWriteBytes(typeAndCrc[1..], crc);
        _out.Write(typeAndCrc[1..]);
    }

    public TableInfo Finish(string fileName, ulong maxSequence)
    {
        if (_finished) throw new InvalidOperationException("Table is already finished.");
        _finished = true;
        FlushBlock();

        var meta = new BlockBuilder(1);
        Span<byte> seqBuf = stackalloc byte[10];
        var w = Varint.WriteUInt64(seqBuf, maxSequence);
        meta.Add(Encoding.ASCII.GetBytes("lsm.maxseq"), seqBuf[..w].ToArray());
        var metaBytes = meta.Finish();
        var metaOffset = _out.Position;
        WriteBlock(metaBytes);

        var indexBytes = _index.Finish();
        var indexOffset = _out.Position;
        WriteBlock(indexBytes);

        WriteHandlePadded(new BlockHandle(metaOffset, metaBytes.Length));
        WriteHandlePadded(new BlockHandle(indexOffset, indexBytes.Length));
        _out.Write(Encoding.ASCII.GetBytes(Magic));
        _out.Flush(flushToDisk: true);

        string MinMax(byte[]? internalKey) =>
            internalKey is null
                ? ""
                : Encoding.UTF8.GetString(InternalKey.Decode(internalKey).UserKey);

        return new TableInfo(
            fileName,
            MinMax(_minKey),
            MinMax(_maxKey),
            maxSequence,
            _entries,
            _out.Position);
    }

    private void WriteHandlePadded(BlockHandle handle)
    {
        var encoded = handle.Encode();
        if (encoded.Length > 20) throw new InvalidOperationException("Block handle does not fit its 20-byte footer slot.");
        _out.Write(encoded);
        _out.Write(new byte[20 - encoded.Length]);
    }

    public void Dispose() => _out.Dispose();
}

public sealed class SSTableReader : IDisposable
{
    private readonly FileStream _file;
    private readonly List<KeyValuePair<byte[], BlockHandle>> _index;
    private readonly ulong _maxSequence;
    private long _cachedOffset = -1;
    private Block? _cachedBlock;

    public ulong MaxSequence => _maxSequence;
    public string Path { get; }

    private SSTableReader(string path, FileStream file, List<KeyValuePair<byte[], BlockHandle>> index, ulong maxSequence)
    {
        Path = path;
        _file = file;
        _index = index;
        _maxSequence = maxSequence;
    }

    public static SSTableReader Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (file.Length < 48)
                throw new InvalidDataException("File is too small for an SSTable footer.");
            file.Seek(-48, SeekOrigin.End);
            var footer = new byte[48];
            ReadFull(file, footer);
            if (Encoding.ASCII.GetString(footer, 40, 8) != SSTableWriter.Magic)
                throw new InvalidDataException("Bad SSTable magic — not an LSMSTORE table file.");

            var metaHandle = BlockHandle.Decode(footer.AsSpan(0, 20), out _);
            var indexHandle = BlockHandle.Decode(footer.AsSpan(20, 20), out _);

            var metaBlock = ReadBlock(file, metaHandle);
            var maxSequence = 0UL;
            foreach (var (key, value) in metaBlock.Entries())
            {
                if (Encoding.ASCII.GetString(key) == "lsm.maxseq")
                    maxSequence = Varint.ReadUInt64(value, out _);
            }

            var indexBlock = ReadBlock(file, indexHandle);
            var index = new List<KeyValuePair<byte[], BlockHandle>>();
            foreach (var (key, value) in indexBlock.Entries())
                index.Add(new KeyValuePair<byte[], BlockHandle>(key, BlockHandle.Decode(value, out _)));

            return new SSTableReader(path, file, index, maxSequence);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Exact lookup by internal key; null when absent.</summary>
    public byte[]? Get(byte[] internalKey)
    {
        var comparer = new InternalKey.Comparer();
        // First index entry whose key >= target names the block that can hold it.
        var lo = 0;
        var hi = _index.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (comparer.Compare(_index[mid].Key, internalKey) >= 0) { found = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        if (found < 0) return null;
        var block = ReadDataBlock(_index[found].Value);
        return block.Get(internalKey, comparer);
    }

    public IEnumerable<KeyValuePair<byte[], byte[]>> Entries()
    {
        foreach (var (_, handle) in _index)
        {
            var block = ReadDataBlock(handle);
            foreach (var entry in block.Entries())
                yield return entry;
        }
    }

    private Block ReadDataBlock(BlockHandle handle)
    {
        if (_cachedBlock is not null && _cachedOffset == handle.Offset)
            return _cachedBlock;
        var block = ReadBlock(_file, handle);
        _cachedOffset = handle.Offset;
        _cachedBlock = block;
        return block;
    }

    private static Block ReadBlock(FileStream file, BlockHandle handle)
    {
        file.Seek(handle.Offset, SeekOrigin.Begin);
        var bytes = new byte[handle.Size];
        ReadFull(file, bytes);
        var trailer = new byte[5];
        ReadFull(file, trailer);
        if (trailer[0] != 0)
            throw new InvalidDataException("Compressed SSTable blocks are not supported.");
        var expectedCrc = BitConverter.ToUInt32(trailer, 1);
        var crcInput = new byte[bytes.Length + 1];
        bytes.CopyTo(crcInput, 0);
        if (Crc32.Compute(crcInput) != expectedCrc)
            throw new InvalidDataException("SSTable block CRC mismatch — file is corrupt.");
        return Block.Parse(bytes);
    }

    private static void ReadFull(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) throw new EndOfStreamException("Unexpected end of SSTable file.");
            total += read;
        }
    }

    public void Dispose()
    {
        _file.Dispose();
    }
}
