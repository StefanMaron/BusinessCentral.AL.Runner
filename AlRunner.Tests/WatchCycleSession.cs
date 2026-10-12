using System.Diagnostics;

namespace AlRunner.Tests;

/// <summary>A `--watch` process and its output. A cycle's text is read as "everything since the
/// quiet before the edit", after the output has been quiet again, never as a window closed by
/// the marker's index: the [watch] FULL REBUILD line goes to stderr and the closing marker to
/// stdout, and list order is pump order, not program order (#1843, #2653).</summary>
internal sealed class WatchCycleSession : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private readonly List<CapturedLine> _lines = new();
    private readonly Process _p;

    public WatchCycleSession(string bundleArguments, string cacheDir)
    {
        _p = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")) + TestBuildConfig.BcVersionArg
                + $" {bundleArguments} --watch --show-pass --cache \"{cacheDir}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        })!;
        Pump(_p.StandardOutput, OutputStream.Stdout);
        Pump(_p.StandardError, OutputStream.Stderr);
    }

    private void Pump(StreamReader r, OutputStream stream) => Task.Run(async () =>
    {
        string? l;
        while ((l = await r.ReadLineAsync()) != null) lock (_lines) _lines.Add(new CapturedLine(stream, l));
    });

    private string DumpAll() { lock (_lines) return string.Join("\n", _lines.Select((l, i) => $"[{i}][{l.Stream}] {l.Text}")); }

    private async Task WaitUntilQuiet()
    {
        int seen;
        do
        {
            lock (_lines) seen = _lines.Count;
            await Task.Delay(1500);
            if (_p.HasExited) throw new TimeoutException($"watch exited early (exit={_p.ExitCode}).\n{DumpAll()}");
        } while (_lines.Count != seen);
    }

    /// <summary>Runs <paramref name="edit"/> (nothing for the first cycle) and returns the text
    /// of the cycle it started.</summary>
    public async Task<string> Cycle(Action? edit)
    {
        int start;
        lock (_lines) start = _lines.Count;
        edit?.Invoke();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(240);
        while (true)
        {
            List<int> found;
            lock (_lines)
                found = WatchOutputSlicing.FindStdoutMarkerIndices(_lines, WatchOutputSlicing.WaitingForSourceMarker, start);
            if (found.Count > 0) break;
            if (_p.HasExited) throw new TimeoutException($"watch exited early (exit={_p.ExitCode}).\n{DumpAll()}");
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"watch marker not seen.\n{DumpAll()}");
            await Task.Delay(200);
        }
        await WaitUntilQuiet();
        lock (_lines) return WatchOutputSlicing.MergedJoin(_lines, start, _lines.Count);
    }

    public void Dispose()
    {
        try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch { }
        _p.Dispose();
    }
}
