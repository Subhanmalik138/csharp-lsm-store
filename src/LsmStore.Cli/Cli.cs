// `lsm` — the command-line front end for LsmStore.
// Testable by design: Run takes its writers and returns a process exit code,
// so the test suite drives it in-process instead of spawning a shell.
namespace LsmStore.Cli;

using System.Diagnostics;
using LsmStore;

public static class LsmCli
{
    public static int Run(string[] args, TextWriter @out, TextWriter err)
    {
        var dir = "./lsm-data";
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dir" && i + 1 < args.Length)
            {
                dir = args[++i];
            }
            else if (args[i].StartsWith("--dir=", StringComparison.Ordinal))
            {
                dir = args[i]["--dir=".Length..];
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        if (rest.Count == 0)
        {
            PrintUsage(err);
            return 2;
        }

        try
        {
            using var db = LsmDb.Open(dir);
            return rest[0] switch
            {
                "put" => Put(db, rest, @out, err),
                "get" => Get(db, rest, @out, err),
                "del" => Delete(db, rest, @out, err),
                "scan" => Scan(db, rest, @out),
                "flush" => Flush(db, @out),
                "compact" => Compact(db, @out),
                "info" => Info(db, @out),
                "bench" => Bench(db, rest, @out, err),
                _ => Unknown(rest[0], err),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            err.WriteLine($"lsm: {ex.Message}");
            return 1;
        }
    }

    private static int Put(LsmDb db, List<string> args, TextWriter @out, TextWriter err)
    {
        if (args.Count != 3) { err.WriteLine("usage: lsm put <key> <value>"); return 2; }
        db.Put(args[1], args[2]);
        @out.WriteLine("ok");
        return 0;
    }

    private static int Get(LsmDb db, List<string> args, TextWriter @out, TextWriter err)
    {
        if (args.Count != 2) { err.WriteLine("usage: lsm get <key>"); return 2; }
        var value = db.Get(args[1]);
        if (value is null)
        {
            err.WriteLine("not found");
            return 1;
        }
        @out.WriteLine(value);
        return 0;
    }

    private static int Delete(LsmDb db, List<string> args, TextWriter @out, TextWriter err)
    {
        if (args.Count != 2) { err.WriteLine("usage: lsm del <key>"); return 2; }
        @out.WriteLine(db.Delete(args[1]) ? "deleted" : "not found");
        return 0;
    }

    private static int Scan(LsmDb db, List<string> args, TextWriter @out)
    {
        string? from = null, to = null, prefix = null;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--from" && i + 1 < args.Count) from = args[++i];
            else if (args[i] == "--to" && i + 1 < args.Count) to = args[++i];
            else if (args[i] == "--prefix" && i + 1 < args.Count) prefix = args[++i];
        }
        foreach (var (key, value) in db.Scan(from, to, prefix))
            @out.WriteLine($"{key}\t{value}");
        return 0;
    }

    private static int Flush(LsmDb db, TextWriter @out)
    {
        db.Flush();
        @out.WriteLine("flushed");
        return 0;
    }

    private static int Compact(LsmDb db, TextWriter @out)
    {
        db.Compact();
        @out.WriteLine("compacted");
        return 0;
    }

    private static int Info(LsmDb db, TextWriter @out)
    {
        var info = db.Info();
        @out.WriteLine($"directory:      {info.Directory}");
        @out.WriteLine($"memtable:       {info.MemTableEntries} entries (~{info.MemTableBytes} bytes)");
        @out.WriteLine($"sstables:       {info.TableCount} ({info.TotalTableBytes} bytes)");
        @out.WriteLine($"total entries:  {info.TotalEntries}");
        @out.WriteLine($"next sequence:  {info.NextSequence}");
        return 0;
    }

    private static int Bench(LsmDb db, List<string> args, TextWriter @out, TextWriter err)
    {
        var n = 10_000;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--n" && i + 1 < args.Count && !int.TryParse(args[++i], out n))
            {
                err.WriteLine("usage: lsm bench [--n <count>]");
                return 2;
            }
        }

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
            db.Put($"bench:{i:D7}", $"value-{i}");
        var putMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var hits = 0;
        for (var i = 0; i < n; i++)
        {
            if (db.Get($"bench:{i:D7}") is not null) hits++;
        }
        var getMs = sw.ElapsedMilliseconds;

        @out.WriteLine($"puts: {n} in {putMs} ms ({(n * 1000.0 / Math.Max(1, putMs)):F0}/s)");
        @out.WriteLine($"gets: {hits}/{n} in {getMs} ms ({(n * 1000.0 / Math.Max(1, getMs)):F0}/s)");
        return 0;
    }

    private static int Unknown(string command, TextWriter err)
    {
        err.WriteLine($"lsm: unknown command '{command}'");
        PrintUsage(err);
        return 2;
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("usage: lsm [--dir <path>] <command> [args]");
        w.WriteLine("  put <key> <value>     store a value");
        w.WriteLine("  get <key>             print a value (exit 1 when missing)");
        w.WriteLine("  del <key>             delete a key");
        w.WriteLine("  scan [--from K] [--to K] [--prefix P]");
        w.WriteLine("  flush                 write the memtable to an SSTable");
        w.WriteLine("  compact               merge all SSTables into one");
        w.WriteLine("  info                  show database statistics");
        w.WriteLine("  bench [--n <count>]   measure put/get throughput");
    }
}
