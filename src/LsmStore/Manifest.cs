// The manifest is the database's source of truth: which SSTables exist
// (newest first), the next sequence number, the next file number, and the
// current WAL. It is rewritten atomically (temp file + rename) on every
// flush and compaction, and it is written BEFORE old files are deleted, so a
// crash can only ever orphan files — never lose data the manifest claims.
namespace LsmStore;

using System.Text.Json;

public sealed record ManifestTable(
    string FileName,
    string MinKey,
    string MaxKey,
    ulong MaxSequence,
    long Entries,
    long SizeBytes);

public sealed class Manifest
{
    public ulong NextSequence { get; set; } = 1;
    public long NextFileNumber { get; set; } = 1;
    public string CurrentWal { get; set; } = "000001.log";

    /// <summary>SSTables, newest first — lookup order for point reads.</summary>
    public List<ManifestTable> Tables { get; set; } = new();

    public static Manifest? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Manifest>(json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public void Save(string directory)
    {
        var path = Path.Combine(directory, "MANIFEST");
        var tempPath = Path.Combine(directory, "MANIFEST.tmp");
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}
