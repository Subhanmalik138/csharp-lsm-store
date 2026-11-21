using LsmStore;

namespace LsmStore.Tests;

public sealed class LsmDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lsm-db-" + Guid.NewGuid().ToString("N"));

    // Small memtable + low compaction threshold so flushes and merges happen
    // in the test instead of needing thousands of keys.
    private static LsmOptions TestOptions() => new()
    {
        MemTableBytes = 512,
        BlockSizeBytes = 256,
        CompactionThreshold = 3,
    };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void PutGetDelete_RoundTrips()
    {
        using var db = LsmDb.Open(_dir);
        db.Put("name", "ada");
        Assert.Equal("ada", db.Get("name"));
        Assert.True(db.Delete("name"));
        Assert.Null(db.Get("name"));
        Assert.False(db.Delete("name"));
    }

    [Fact]
    public void Overwrite_KeepsLatestValue()
    {
        using var db = LsmDb.Open(_dir);
        db.Put("k", "v1");
        db.Put("k", "v2");
        Assert.Equal("v2", db.Get("k"));
    }

    [Fact]
    public void DataSurvivesReopen_FromWal()
    {
        using (var db = LsmDb.Open(_dir))
        {
            db.Put("a", "1");
            db.Put("b", "2");
        }
        using var reopened = LsmDb.Open(_dir);
        Assert.Equal("1", reopened.Get("a"));
        Assert.Equal("2", reopened.Get("b"));
    }

    [Fact]
    public void DataSurvivesReopen_FromSSTable()
    {
        using (var db = LsmDb.Open(_dir))
        {
            db.Put("a", "1");
            db.Flush();
            db.Put("b", "2");
            db.Flush();
        }
        using var reopened = LsmDb.Open(_dir);
        Assert.Equal("1", reopened.Get("a"));
        Assert.Equal("2", reopened.Get("b"));
        Assert.Null(reopened.Get("missing"));
    }

    [Fact]
    public void DeleteAcrossFlush_ShadowsOlderTable()
    {
        using (var db = LsmDb.Open(_dir))
        {
            db.Put("k", "v");
            db.Flush();
            db.Delete("k");
            db.Flush();
        }
        using var reopened = LsmDb.Open(_dir);
        Assert.Null(reopened.Get("k"));
    }

    [Fact]
    public void AutoFlush_TriggersOnMemtableSize()
    {
        using var db = LsmDb.Open(_dir, TestOptions());
        for (var i = 0; i < 50; i++)
            db.Put($"key:{i:D2}", new string('x', 40));
        var info = db.Info();
        Assert.True(info.TableCount >= 1, "memtable should have flushed to an SSTable");
        for (var i = 0; i < 50; i++)
            Assert.Equal(new string('x', 40), db.Get($"key:{i:D2}"));
    }

    [Fact]
    public void Scan_ReturnsOrderedLiveEntries()
    {
        using var db = LsmDb.Open(_dir);
        db.Put("b", "2");
        db.Put("a", "1");
        db.Put("c", "3");
        db.Delete("b");
        db.Put("d", "4");
        var keys = db.Scan().Select(e => e.Key).ToArray();
        Assert.Equal(new[] { "a", "c", "d" }, keys);
    }

    [Fact]
    public void Scan_RespectsFromToAndPrefix()
    {
        using var db = LsmDb.Open(_dir);
        foreach (var k in new[] { "user:1", "user:2", "user:3", "post:1" })
            db.Put(k, "v");
        Assert.Equal(new[] { "user:2", "user:3" }, db.Scan("user:2", "user:4").Select(e => e.Key).ToArray());
        Assert.Equal(new[] { "user:1", "user:2", "user:3" }, db.Scan(prefix: "user:").Select(e => e.Key).ToArray());
    }

    [Fact]
    public void Compaction_PreservesDataAndDropsTombstones()
    {
        using var db = LsmDb.Open(_dir, TestOptions());
        for (var i = 0; i < 30; i++)
            db.Put($"k:{i:D2}", $"v{i}");
        db.Flush();
        for (var i = 0; i < 15; i++)
            db.Delete($"k:{i:D2}");
        db.Put("k:00", "resurrected");
        db.Flush();
        db.Compact();

        var info = db.Info();
        Assert.Equal(1, info.TableCount);
        Assert.Equal("resurrected", db.Get("k:00"));
        for (var i = 1; i < 15; i++)
            Assert.Null(db.Get($"k:{i:D2}"));
        for (var i = 15; i < 30; i++)
            Assert.Equal($"v{i}", db.Get($"k:{i:D2}"));
        Assert.Equal(new[] { "k:00", "k:15", "k:16" },
            db.Scan("k:00", "k:17").Select(e => e.Key).Take(3).ToArray());
    }

    [Fact]
    public void ReopenAfterCompaction_KeepsWorking()
    {
        using (var db = LsmDb.Open(_dir, TestOptions()))
        {
            for (var i = 0; i < 20; i++)
                db.Put($"k:{i:D2}", "v");
            db.Compact();
        }
        using var reopened = LsmDb.Open(_dir);
        for (var i = 0; i < 20; i++)
            Assert.Equal("v", reopened.Get($"k:{i:D2}"));
        reopened.Put("new", "key");
        Assert.Equal("key", reopened.Get("new"));
    }

    [Fact]
    public void ConcurrentPuts_AllVisible()
    {
        using var db = LsmDb.Open(_dir, TestOptions());
        Parallel.For(0, 200, i => db.Put($"ck:{i:D3}", $"v{i}"));
        for (var i = 0; i < 200; i++)
            Assert.Equal($"v{i}", db.Get($"ck:{i:D3}"));
    }

    [Fact]
    public void SequenceNumbers_KeepGrowingAcrossReopen()
    {
        using (var db = LsmDb.Open(_dir))
        {
            db.Put("a", "1");
            db.Flush();
        }
        using var reopened = LsmDb.Open(_dir);
        reopened.Put("a", "2"); // must not reuse sequence 1
        Assert.Equal("2", reopened.Get("a"));
        reopened.Flush();
        using var third = LsmDb.Open(_dir);
        Assert.Equal("2", third.Get("a"));
    }
}
