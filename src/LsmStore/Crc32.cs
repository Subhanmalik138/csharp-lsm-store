// CRC-32 (IEEE 802.3, polynomial 0xEDB88320) — the same checksum family
// LevelDB uses (masked variant). Used to protect WAL records and SSTable
// blocks so torn writes and bit rot are detected instead of silently read.
namespace LsmStore;

public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    public static uint Compute(byte[] data, int offset, int count) =>
        Compute(new ReadOnlySpan<byte>(data, offset, count));
}
