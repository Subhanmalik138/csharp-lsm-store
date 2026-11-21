using LsmStore.Cli;

namespace LsmStore.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lsm-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var @out = new StringWriter();
        var err = new StringWriter();
        var exit = LsmCli.Run(new[] { "--dir", _dir }.Concat(args).ToArray(), @out, err);
        return (exit, @out.ToString(), err.ToString());
    }

    [Fact]
    public void PutGet_RoundTripsAcrossProcesses()
    {
        Assert.Equal(0, Run("put", "hello", "world").Exit);
        var get = Run("get", "hello");
        Assert.Equal(0, get.Exit);
        Assert.Equal("world" + Environment.NewLine, get.Out);
    }

    [Fact]
    public void GetMissing_ExitsOne()
    {
        var result = Run("get", "nope");
        Assert.Equal(1, result.Exit);
        Assert.Contains("not found", result.Err);
    }

    [Fact]
    public void Scan_ListsKeysInOrder()
    {
        Run("put", "b", "2");
        Run("put", "a", "1");
        var scan = Run("scan");
        Assert.Equal(0, scan.Exit);
        Assert.Equal($"a\t1{Environment.NewLine}b\t2{Environment.NewLine}", scan.Out);
    }

    [Fact]
    public void FlushCompactAndInfo_Work()
    {
        Run("put", "k", "v");
        Assert.Equal(0, Run("flush").Exit);
        Assert.Equal(0, Run("compact").Exit);
        var info = Run("info");
        Assert.Equal(0, info.Exit);
        Assert.Contains("sstables:", info.Out);
        Assert.Contains("total entries:  1", info.Out);
    }

    [Fact]
    public void UnknownCommand_ExitsTwo()
    {
        Assert.Equal(2, Run("explode").Exit);
    }

    [Fact]
    public void NoCommand_ExitsTwo()
    {
        var @out = new StringWriter();
        var err = new StringWriter();
        Assert.Equal(2, LsmCli.Run(Array.Empty<string>(), @out, err));
    }
}
