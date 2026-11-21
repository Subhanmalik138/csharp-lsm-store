using System.Text;
using LsmStore;

namespace LsmStore.Tests;

public sealed class Crc32Tests
{
    [Fact]
    public void KnownVector_MatchesIeeeCheckValue()
    {
        // The standard CRC-32 check value for "123456789".
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void EmptyInput_HasDefinedValue()
    {
        Assert.Equal(0u, Crc32.Compute(Array.Empty<byte>()));
    }

    [Fact]
    public void SingleBitChange_ChangesChecksum()
    {
        var a = Crc32.Compute(Encoding.ASCII.GetBytes("hello"));
        var b = Crc32.Compute(Encoding.ASCII.GetBytes("hellp"));
        Assert.NotEqual(a, b);
    }
}

public sealed class VarintTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(300u)]
    [InlineData(uint.MaxValue)]
    public void UInt32_RoundTrips(uint value)
    {
        Span<byte> buf = stackalloc byte[10];
        var written = Varint.WriteUInt32(buf, value);
        var read = Varint.ReadUInt32(buf[..written], out var bytesRead);
        Assert.Equal(value, read);
        Assert.Equal(written, bytesRead);
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(ulong.MaxValue)]
    [InlineData(1ul << 63)]
    public void UInt64_RoundTrips(ulong value)
    {
        Span<byte> buf = stackalloc byte[10];
        var written = Varint.WriteUInt64(buf, value);
        var read = Varint.ReadUInt64(buf[..written], out var bytesRead);
        Assert.Equal(value, read);
        Assert.Equal(written, bytesRead);
    }

    [Fact]
    public void SmallValues_EncodeInOneByte()
    {
        Assert.Equal(1, Varint.SizeOf(42));
        Assert.Equal(2, Varint.SizeOf(300));
    }

    [Fact]
    public void TruncatedInput_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Varint.ReadUInt32(new byte[] { 0x80 }, out _));
    }
}

public sealed class InternalKeyTests
{
    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        var key = Encoding.UTF8.GetBytes("user:42");
        var encoded = InternalKey.Encode(key, 7, EntryType.Put);
        var (userKey, seq, type) = InternalKey.Decode(encoded);
        Assert.Equal(key, userKey);
        Assert.Equal(7ul, seq);
        Assert.Equal(EntryType.Put, type);
    }

    [Fact]
    public void Ordering_IsKeyAscendingThenSequenceDescending()
    {
        var comparer = new InternalKey.Comparer();
        var aOld = InternalKey.Encode(Encoding.UTF8.GetBytes("k"), 5, EntryType.Put);
        var aNew = InternalKey.Encode(Encoding.UTF8.GetBytes("k"), 9, EntryType.Put);
        var b = InternalKey.Encode(Encoding.UTF8.GetBytes("k2"), 1, EntryType.Put);

        Assert.True(comparer.Compare(aNew, aOld) < 0, "newer version sorts first");
        Assert.True(comparer.Compare(aOld, b) < 0, "shorter key sorts first");
        Assert.Equal(0, comparer.Compare(aOld, aOld.ToArray()));
    }

    [Fact]
    public void DeleteAndPut_WithSameSequence_DifferByType()
    {
        var del = InternalKey.Encode(Encoding.UTF8.GetBytes("k"), 3, EntryType.Delete);
        var put = InternalKey.Encode(Encoding.UTF8.GetBytes("k"), 3, EntryType.Put);
        Assert.NotEqual(0, InternalKey.Compare(del, put));
    }
}
