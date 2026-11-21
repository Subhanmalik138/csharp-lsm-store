namespace LsmStore;

/// <summary>Tuning knobs for a database instance. Defaults suit a small CLI
/// workload; raise MemTableBytes / CompactionThreshold for bulk loads.</summary>
public sealed class LsmOptions
{
    /// <summary>Flush the memtable to an SSTable once it passes this size.</summary>
    public long MemTableBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Entries per SSTable block restart interval (prefix compression).</summary>
    public int BlockRestartInterval { get; init; } = 16;

    /// <summary>Target size of one SSTable data block.</summary>
    public int BlockSizeBytes { get; init; } = 4096;

    /// <summary>Merge all SSTables into one when the count reaches this.</summary>
    public int CompactionThreshold { get; init; } = 4;
}
