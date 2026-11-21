# LsmStore — a persistent LSM-tree key-value store in C#

A small, dependency-free LSM-tree storage engine written from scratch in C#
(.NET 8), in the spirit of LevelDB / RocksDB. Keys and values are UTF-8
strings ordered byte-wise; every write carries a sequence number and deletes
are tombstones, so the newest version of a key always wins.

```text
                 ┌──────────────┐
  Put/Get/Delete │   LsmDb      │  one writer lock, many readers
                 └──────┬───────┘
                        │
            ┌───────────┴───────────┐
            ▼                       ▼
     ┌────────────┐          ┌──────────────┐
     │  MemTable  │          │  SSTables    │  newest first
     │  (sorted,  │  flush   │  (immutable, │
     │   in-mem)  │ ───────► │   on disk)   │
     └─────┬──────┘          └──────┬───────┘
           │ WAL (every write,      │ compaction merges
           │ CRC-framed, replayed   │ all tables into one
           │ on open)               │ and drops tombstones
           └────────────────────────┘
```

## How it works

- **Memtable** — an in-memory sorted table holding one entry per key (the
  newest sequence wins). It flushes to disk once it passes
  `MemTableBytes` (default 4 MB).
- **Write-ahead log** — every mutation is appended to the WAL first, framed
  as `[crc32][length][payload]`. On open the log is replayed into a fresh
  memtable; a torn tail (crash mid-write) stops replay at the first bad CRC
  and is truncated, so a half-written mutation is never resurrected.
- **SSTables** — immutable sorted files in a LevelDB-style layout: data
  blocks with prefix-compressed entries and restart points, a sparse index
  block mapping each block's last key to its handle, a metaindex carrying
  the table's max sequence, and a 48-byte footer with the magic `LSMSTORE`.
  Every block carries a CRC32, so bit rot is detected on read, not silently
  served.
- **Internal keys** — a user key plus an 8-byte trailer
  `(sequence << 8) | type`, ordered key-ascending then sequence-descending.
  That single ordering rule makes point lookups, scans, and compaction
  merges all fall out of one comparison.
- **Compaction** — size-tiered: when the table count reaches
  `CompactionThreshold` (default 4) all tables merge into one, dropping
  shadowed versions and tombstones. A full merge sees all live data, so
  dropping tombstones there is safe.
- **Manifest** — a small JSON file naming the live SSTables, the next
  sequence, and the current WAL. It is rewritten atomically (temp file +
  rename) *before* old files are deleted, so a crash can only orphan files,
  never lose data the manifest claims.

## Design decisions

1. **Sequence numbers instead of timestamps.** A monotonic counter gives a
   total order across memtable, WAL, and every SSTable, with no clock
   skew to reason about and no dependence on wall time.
2. **Tombstones instead of in-place deletes.** Deletes are just another
   version. An older SSTable may still hold the value, and only the
   tombstone proves it is gone — the same trick real LSM stores use.
3. **CRC on everything durable.** WAL records and SSTable blocks are both
   checksummed. A corrupt file fails loudly at open/read time instead of
   returning wrong data.
4. **No block cache.** SSTable readers re-read blocks from disk and rely on
   the OS page cache. A one-entry cache would need locking to be safe under
   the shared read lock, and the added complexity buys nothing at this
   scale.
5. **Deliberate simplifications, documented as such:** keys/values are
   strings (no binary API); blocks are uncompressed; scans merge sources
   into a sorted dictionary instead of a lazy k-way iterator; compaction is
   a full merge rather than leveled. Each is a clear extension point, not a
   hidden assumption.

## Project layout

```text
src/
  LsmStore/            # the engine (no dependencies)
    LsmDb.cs           # public API: Put/Get/Delete/Scan/Flush/Compact
    MemTable.cs        # sorted in-memory table + tombstones
    Wal.cs             # CRC-framed write-ahead log + torn-tail recovery
    SSTable.cs         # SSTable writer/reader, footer, block handles
    Block.cs           # prefix-compressed blocks with restart points
    InternalKey.cs     # user key + sequence + type encoding and ordering
    Manifest.cs        # atomic JSON manifest (source of truth)
    Crc32.cs Varint.cs # checksums and LEB128 varints
  LsmStore.Cli/        # `lsm` command-line front end
tests/
  LsmStore.Tests/      # 53 xUnit tests
```

## Run it

```bash
dotnet build -c Release

# store and read values (data lives in ./lsm-data by default)
dotnet src/LsmStore.Cli/bin/Release/net8.0/LsmStore.Cli.dll put user:1 ada --dir ./data
dotnet src/LsmStore.Cli/bin/Release/net8.0/LsmStore.Cli.dll get user:1 --dir ./data
dotnet src/LsmStore.Cli/bin/Release/net8.0/LsmStore.Cli.dll del user:1 --dir ./data

# ordered scans
dotnet src/LsmStore.Cli/bin/Release/net8.0/LsmStore.Cli.dll scan --prefix user: --dir ./data
dotnet src/LsmStore.Cli/bin/Release/net8.0/LsmStore.Cli.dll scan --from user:2 --to user:9 --dir ./data

# maintenance and stats
dotnet src/LsmStore.Cli/bin/Release/net8.0/LsmStore.Cli.dll flush --dir ./data
dotnet src/LsmStore.Cli/bin/Release/net8.0/LsmStore.Cli.dll compact --dir ./data
dotnet src/LsmStore.Cli/bin/Release/net8.0/LsmStore.Cli.dll info --dir ./data

# throughput
dotnet src/LsmStore.Cli/bin/Release/net8.0/LsmStore.Cli.dll bench --n 5000 --dir ./bench-data
```

Or publish a single self-contained binary:

```bash
dotnet publish src/LsmStore.Cli -c Release -o ./publish
./publish/LsmStore.Cli put hello world --dir ./data
```

## Run the tests

```bash
dotnet test
```

53 tests cover CRC/varint vectors, internal-key ordering, memtable
semantics, WAL replay and torn-tail recovery, SSTable round-trips across
multiple blocks, CRC corruption detection, persistence across reopen (from
both WAL and SSTables), flush/compaction correctness, concurrent puts, and
the CLI end to end.

Measured on this machine (`lsm bench --n 5000`): ~122,000 puts/sec and
~714,000 gets/sec. Puts flush the WAL to the OS per record and fsync on
close/flush; gets are served from the memtable and the page cache.

## Tech stack

- C# 12 / .NET 8, zero runtime dependencies
- xUnit + Microsoft.NET.Test.Sdk for tests
- No external packages in the engine itself — the on-disk format, the
  checksums, and the varint codec are all hand-rolled
