# LsmStore — a persistent LSM-tree key-value store in C#

A small, dependency-free LSM-tree storage engine: a write-ahead log, an
in-memory memtable, immutable sorted SSTables with prefix-compressed blocks,
and size-tiered compaction. Keys and values are UTF-8 strings ordered
byte-wise; every write carries a sequence number and deletes are tombstones,
so the newest version of a key always wins.

> Work in progress — the engine is being built in stages; see the commit
> history for the build story.
