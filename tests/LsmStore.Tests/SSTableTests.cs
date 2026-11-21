using System.Text;
using LsmStore;

namespace LsmStore.Tests;

public sealed class SSTableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lsm-sst-" + Guid.NewGuid().ToString("N"));
    private readonly LsmOptions _options = new() { BlockSizeBytes = 256, BlockRestartInterval = 4 };

    public SSTableTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteTable(params (string Key, string? Value, ulong Seq)[] rows)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".sst");
        using var writer = new SSTableWriter(path, _options);
        var maxSeq = 0ul;
        foreach (var (key, value, seq) in rows)
        {
            var ik = InternalKey.Encode(Encoding.UTF8.GetBytes(key), seq, value is null ? EntryType.Delete : EntryType.Put);
            writer.Add(ik, value is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value));
            maxSeq = Math.Max(maxSeq, seq);
        }
        writer.Finish(Path.GetFileName(path), maxSeq);
        return path;
    }

    [Fact]
    public void RoundTrip_PreservesEntriesAndOrder()
    {
        var path = WriteTable(("a", "1", 1), ("b", "2", 2), ("c", "3", 3));
        using var reader = SSTableReader.Open(path);
        Assert.Equal(3ul, reader.MaxSequence);
        var keys = reader.Entries()
            .Select(e => Encoding.UTF8.GetString(InternalKey.Decode(e.Key).UserKey))
            .ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, keys);
    }

    [Fact]
    public void GetUserKey_ReturnsNewestVersion()
    {
        // Entries must be added in internal-key order (key asc, sequence desc),
        // as the memtable and the compaction merge both guarantee.
        var path = WriteTable(("k", "new", 5), ("k", "old", 1), ("other", "x", 2));
        using var reader = SSTableReader.Open(path);
        var hit = reader.GetUserKey("k"u8.ToArray());
        Assert.NotNull(hit);
        Assert.Equal(5ul, hit.Value.Sequence);
        Assert.Equal("new", Encoding.UTF8.GetString(hit.Value.Value));
    }

    [Fact]
    public void GetUserKey_ReturnsTombstoneForDeletes()
    {
        var path = WriteTable(("k", null, 4));
        using var reader = SSTableReader.Open(path);
        var hit = reader.GetUserKey("k"u8.ToArray());
        Assert.NotNull(hit);
        Assert.Equal(EntryType.Delete, hit.Value.Type);
    }

    [Fact]
    public void GetUserKey_ReturnsNullForMissingKey()
    {
        var path = WriteTable(("a", "1", 1), ("c", "3", 3));
        using var reader = SSTableReader.Open(path);
        Assert.Null(reader.GetUserKey("b"u8.ToArray()));
        Assert.Null(reader.GetUserKey("zzz"u8.ToArray()));
    }

    [Fact]
    public void ManyEntries_SpanMultipleBlocks_StillFound()
    {
        var rows = Enumerable.Range(0, 200)
            .Select(i => ($"key:{i:D3}", $"value-{i}", (ulong)i + 1))
            .ToArray();
        var path = WriteTable(rows);
        using var reader = SSTableReader.Open(path);
        for (var i = 0; i < 200; i += 17)
        {
            var hit = reader.GetUserKey(Encoding.UTF8.GetBytes($"key:{i:D3}"));
            Assert.NotNull(hit);
            Assert.Equal($"value-{i}", Encoding.UTF8.GetString(hit.Value.Value));
        }
        Assert.Equal(200, reader.Entries().Count());
    }

    [Fact]
    public void BadMagic_RefusesToOpen()
    {
        var path = Path.Combine(_dir, "garbage.sst");
        File.WriteAllBytes(path, new byte[200]);
        Assert.Throws<InvalidDataException>(() => SSTableReader.Open(path));
    }

    [Fact]
    public void CorruptBlock_DetectedByCrc()
    {
        var path = WriteTable(("a", "1", 1), ("b", "2", 2));
        var bytes = File.ReadAllBytes(path);
        bytes[5] ^= 0xFF; // inside the first data block
        File.WriteAllBytes(path, bytes);
        using var reader = SSTableReader.Open(path);
        Assert.Throws<InvalidDataException>(() => reader.GetUserKey("a"u8.ToArray()));
    }
}
