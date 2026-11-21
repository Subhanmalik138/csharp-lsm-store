// Write-ahead log. One record per mutation, framed as
// [crc32 LE][length LE][payload]; payload is
// [1-byte type][8-byte LE sequence][varint key-len][key][varint value-len][value].
// A crash mid-write leaves a torn tail record; replay stops at the first bad
// CRC or short read and the caller truncates the file there, so recovery
// never resurrects a half-written mutation.
namespace LsmStore;

public sealed record WalRecord(EntryType Type, ulong Sequence, byte[] Key, byte[] Value);

public sealed class WalWriter : IDisposable
{
    private readonly FileStream _stream;

    public WalWriter(string path)
    {
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public void Append(EntryType type, ulong sequence, byte[] key, byte[] value)
    {
        using var payload = new MemoryStream();
        payload.WriteByte((byte)type);
        Span<byte> seq = stackalloc byte[8];
        for (var i = 0; i < 8; i++) seq[i] = (byte)(sequence >> (8 * i));
        payload.Write(seq);
        Span<byte> varint = stackalloc byte[10];
        var w = Varint.WriteUInt32(varint, (uint)key.Length);
        payload.Write(varint[..w]);
        payload.Write(key);
        w = Varint.WriteUInt32(varint, (uint)value.Length);
        payload.Write(varint[..w]);
        payload.Write(value);

        var bytes = payload.ToArray();
        var crc = Crc32.Compute(bytes);
        Span<byte> header = stackalloc byte[8];
        BitConverter.TryWriteBytes(header[..4], crc);
        BitConverter.TryWriteBytes(header[4..], (uint)bytes.Length);
        _stream.Write(header);
        _stream.Write(bytes);
        _stream.Flush(); // visible to readers; fsync happens on FlushToDisk/Dispose
    }

    public void FlushToDisk() => _stream.Flush(flushToDisk: true);

    public void Dispose()
    {
        FlushToDisk();
        _stream.Dispose();
    }
}

public static class WalReader
{
    private const uint MaxRecordBytes = 64 * 1024 * 1024;

    /// <summary>Replay a log file. Returns the valid records, the highest
    /// sequence seen, and the offset up to which the file is intact.</summary>
    public static (List<WalRecord> Records, ulong MaxSequence, long ValidBytes) Replay(string path)
    {
        var records = new List<WalRecord>();
        ulong maxSequence = 0;
        long validBytes = 0;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[8];
        while (true)
        {
            var read = ReadFull(stream, header, 8);
            if (read == 0) break; // clean EOF
            if (read < 8) break;  // torn header — stop, do not trust the tail
            var crc = BitConverter.ToUInt32(header, 0);
            var length = BitConverter.ToUInt32(header, 4);
            if (length > MaxRecordBytes) break;
            var payload = new byte[length];
            if (ReadFull(stream, payload, (int)length) < length) break; // torn payload
            if (Crc32.Compute(payload) != crc) break; // corrupt record
            var record = Parse(payload);
            if (record is null) break;
            records.Add(record);
            maxSequence = Math.Max(maxSequence, record.Sequence);
            validBytes = stream.Position;
        }
        return (records, maxSequence, validBytes);
    }

    public static void TruncateTo(string path, long validBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.SetLength(validBytes);
    }

    private static int ReadFull(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static WalRecord? Parse(byte[] payload)
    {
        try
        {
            var p = 0;
            var type = (EntryType)payload[p++];
            Span<byte> seqBytes = payload.AsSpan(p, 8);
            ulong sequence = 0;
            for (var i = 0; i < 8; i++) sequence |= (ulong)seqBytes[i] << (8 * i);
            p += 8;
            var keyLen = (int)Varint.ReadUInt32(payload.AsSpan(p), out var n); p += n;
            var key = payload.AsSpan(p, keyLen).ToArray(); p += keyLen;
            var valueLen = (int)Varint.ReadUInt32(payload.AsSpan(p), out n); p += n;
            var value = payload.AsSpan(p, valueLen).ToArray(); p += valueLen;
            if (p != payload.Length) return null;
            return new WalRecord(type, sequence, key, value);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }
}
