using System.Text;
using LsmStore;

namespace LsmStore.Tests;

public sealed class WalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lsm-wal-" + Guid.NewGuid().ToString("N"));

    public WalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AppendAndReplay_RoundTripsRecords()
    {
        var path = Path.Combine(_dir, "test.log");
        using (var wal = new WalWriter(path))
        {
            wal.Append(EntryType.Put, 1, "a"u8.ToArray(), "one"u8.ToArray());
            wal.Append(EntryType.Delete, 2, "b"u8.ToArray(), Array.Empty<byte>());
            wal.Append(EntryType.Put, 3, "c"u8.ToArray(), Encoding.UTF8.GetBytes("three"));
        }

        var (records, maxSeq, validBytes) = WalReader.Replay(path);
        Assert.Equal(3, records.Count);
        Assert.Equal(3ul, maxSeq);
        Assert.Equal(validBytes, new FileInfo(path).Length);
        Assert.Equal(EntryType.Put, records[0].Type);
        Assert.Equal("a", Encoding.UTF8.GetString(records[0].Key));
        Assert.Equal("one", Encoding.UTF8.GetString(records[0].Value));
        Assert.Equal(EntryType.Delete, records[1].Type);
        Assert.Equal("three", Encoding.UTF8.GetString(records[2].Value));
    }

    [Fact]
    public void TornTail_IsDetectedAndTruncatable()
    {
        var path = Path.Combine(_dir, "torn.log");
        using (var wal = new WalWriter(path))
        {
            wal.Append(EntryType.Put, 1, "a"u8.ToArray(), "one"u8.ToArray());
            wal.Append(EntryType.Put, 2, "b"u8.ToArray(), "two"u8.ToArray());
        }
        var goodLength = new FileInfo(path).Length;
        using (var fs = new FileStream(path, FileMode.Append))
        {
            fs.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x10 }); // half a record
        }

        var (records, maxSeq, validBytes) = WalReader.Replay(path);
        Assert.Equal(2, records.Count);
        Assert.Equal(2ul, maxSeq);
        Assert.Equal(goodLength, validBytes);

        WalReader.TruncateTo(path, validBytes);
        Assert.Equal(goodLength, new FileInfo(path).Length);
        Assert.Equal(2, WalReader.Replay(path).Records.Count);
    }

    [Fact]
    public void CorruptCrc_StopsReplayBeforeBadRecord()
    {
        var path = Path.Combine(_dir, "corrupt.log");
        using (var wal = new WalWriter(path))
        {
            wal.Append(EntryType.Put, 1, "a"u8.ToArray(), "one"u8.ToArray());
            wal.Append(EntryType.Put, 2, "b"u8.ToArray(), "two"u8.ToArray());
        }
        // Flip a payload byte in the second record (skip first record's bytes).
        var bytes = File.ReadAllBytes(path);
        var firstLen = BitConverter.ToUInt32(bytes, 4);
        bytes[8 + firstLen + 10] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var (records, _, _) = WalReader.Replay(path);
        Assert.Single(records);
        Assert.Equal("a", Encoding.UTF8.GetString(records[0].Key));
    }

    [Fact]
    public void AppendMode_ContinuesExistingLog()
    {
        var path = Path.Combine(_dir, "append.log");
        using (var wal = new WalWriter(path))
            wal.Append(EntryType.Put, 1, "a"u8.ToArray(), "one"u8.ToArray());
        using (var wal = new WalWriter(path, append: true))
            wal.Append(EntryType.Put, 2, "b"u8.ToArray(), "two"u8.ToArray());

        var (records, _, _) = WalReader.Replay(path);
        Assert.Equal(2, records.Count);
    }
}
