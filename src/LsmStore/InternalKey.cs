// An internal key is a user key plus an 8-byte trailer: the sequence number
// and a 1-byte type (1 = put, 0 = delete), packed as (seq << 8) | type and
// stored little-endian — the LevelDB scheme. Ordering is user key ascending,
// then the trailer descending, so the newest version of a key sorts first
// and a merge scan can keep the first entry it sees per key.
namespace LsmStore;

public enum EntryType : byte
{
    Delete = 0,
    Put = 1,
}

public static class InternalKey
{
    public const int TrailerSize = 8;

    public static ulong Pack(ulong sequence, EntryType type) => (sequence << 8) | (ulong)type;

    public static (ulong Sequence, EntryType Type) Unpack(ulong packed) =>
        (packed >> 8, (EntryType)(packed & 0xFF));

    /// <summary>Encode a user key + sequence + type into an internal key.</summary>
    public static byte[] Encode(byte[] userKey, ulong sequence, EntryType type)
    {
        var result = new byte[userKey.Length + TrailerSize];
        userKey.CopyTo(result, 0);
        var packed = Pack(sequence, type);
        for (var i = 0; i < 8; i++)
            result[userKey.Length + i] = (byte)(packed >> (8 * i));
        return result;
    }

    /// <summary>Split an internal key back into its user key and trailer.</summary>
    public static (byte[] UserKey, ulong Sequence, EntryType Type) Decode(ReadOnlySpan<byte> internalKey)
    {
        if (internalKey.Length < TrailerSize)
            throw new InvalidDataException("Internal key is shorter than its 8-byte trailer.");
        var userKey = internalKey[..^TrailerSize].ToArray();
        ulong packed = 0;
        for (var i = 0; i < 8; i++)
            packed |= (ulong)internalKey[internalKey.Length - TrailerSize + i] << (8 * i);
        var (sequence, type) = Unpack(packed);
        return (userKey, sequence, type);
    }

    /// <summary>Compare internal keys: user key ascending, trailer descending.</summary>
    public static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var aUser = a[..^TrailerSize];
        var bUser = b[..^TrailerSize];
        var cmp = CompareBytes(aUser, bUser);
        if (cmp != 0) return cmp;
        // Same user key: larger packed trailer (newer sequence) sorts first.
        ulong aPacked = 0, bPacked = 0;
        for (var i = 0; i < 8; i++)
        {
            aPacked |= (ulong)a[a.Length - TrailerSize + i] << (8 * i);
            bPacked |= (ulong)b[b.Length - TrailerSize + i] << (8 * i);
        }
        return bPacked.CompareTo(aPacked);
    }

    private static int CompareBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        }
        return a.Length.CompareTo(b.Length);
    }

    public sealed class Comparer : IComparer<byte[]>
    {
        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return Compare(x.AsSpan(), y.AsSpan());
        }
    }
}
