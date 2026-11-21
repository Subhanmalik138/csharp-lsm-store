// Unsigned LEB128 varints, as used by LevelDB / protobuf. Keys, lengths and
// block handles are all varint-encoded so small values cost one byte.
namespace LsmStore;

public static class Varint
{
    public static int WriteUInt32(Span<byte> destination, uint value)
    {
        var i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[i++] = (byte)value;
        return i;
    }

    public static int WriteUInt64(Span<byte> destination, ulong value)
    {
        var i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[i++] = (byte)value;
        return i;
    }

    public static int SizeOf(ulong value)
    {
        var n = 1;
        while (value >= 0x80) { value >>= 7; n++; }
        return n;
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> source, out int bytesRead)
    {
        uint result = 0;
        var shift = 0;
        var i = 0;
        while (true)
        {
            if (i >= source.Length) throw new InvalidDataException("Truncated varint32.");
            byte b = source[i++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift >= 35) throw new InvalidDataException("Varint32 is too long.");
        }
        bytesRead = i;
        return result;
    }

    public static ulong ReadUInt64(ReadOnlySpan<byte> source, out int bytesRead)
    {
        ulong result = 0;
        var shift = 0;
        var i = 0;
        while (true)
        {
            if (i >= source.Length) throw new InvalidDataException("Truncated varint64.");
            byte b = source[i++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift >= 70) throw new InvalidDataException("Varint64 is too long.");
        }
        bytesRead = i;
        return result;
    }
}
