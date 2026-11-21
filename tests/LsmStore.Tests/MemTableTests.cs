using LsmStore;

namespace LsmStore.Tests;

public sealed class MemTableTests
{
    [Fact]
    public void PutThenGet_ReturnsValue()
    {
        var table = new MemTable();
        table.Put("a", 1, "one"u8.ToArray());
        var entry = table.Get("a");
        Assert.NotNull(entry);
        Assert.Equal(EntryType.Put, entry.Type);
        Assert.Equal("one", System.Text.Encoding.UTF8.GetString(entry.Value!));
    }

    [Fact]
    public void Overwrite_KeepsNewestSequenceOnly()
    {
        var table = new MemTable();
        table.Put("a", 1, "old"u8.ToArray());
        table.Put("a", 2, "new"u8.ToArray());
        Assert.Equal(1, table.Count);
        Assert.Equal(2ul, table.Get("a")!.Sequence);
    }

    [Fact]
    public void Delete_LeavesTombstoneNotAbsence()
    {
        var table = new MemTable();
        table.Put("a", 1, "one"u8.ToArray());
        table.Delete("a", 2);
        var entry = table.Get("a");
        Assert.NotNull(entry);
        Assert.Equal(EntryType.Delete, entry.Type);
        Assert.Null(entry.Value);
    }

    [Fact]
    public void GetMissing_ReturnsNull()
    {
        Assert.Null(new MemTable().Get("nope"));
    }

    [Fact]
    public void EntriesInOrder_AreSortedByKey()
    {
        var table = new MemTable();
        table.Put("c", 1, "3"u8.ToArray());
        table.Put("a", 2, "1"u8.ToArray());
        table.Put("b", 3, "2"u8.ToArray());
        var keys = table.EntriesInOrder()
            .Select(e => System.Text.Encoding.UTF8.GetString(InternalKey.Decode(e.Key).UserKey))
            .ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, keys);
    }

    [Fact]
    public void Scan_RespectsRangeBounds()
    {
        var table = new MemTable();
        foreach (var k in new[] { "a", "b", "c", "d" })
            table.Put(k, 1, "v"u8.ToArray());
        var keys = table.Scan("b", "d").Select(e => e.Key).ToArray();
        Assert.Equal(new[] { "b", "c" }, keys);
    }

    [Fact]
    public void ApproximateBytes_GrowsWithContent()
    {
        var table = new MemTable();
        var before = table.ApproximateBytes;
        table.Put("key", 1, new byte[1000]);
        Assert.True(table.ApproximateBytes > before + 1000);
    }
}
