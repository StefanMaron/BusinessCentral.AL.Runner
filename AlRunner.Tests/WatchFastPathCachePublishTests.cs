using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5092, the --watch half: a cycle that took the incremental fast path must not publish its
/// assembly to the AL-output cache. The cache key hashes the source, so an entry stored from a
/// fast-path emit would be served as a HIT to every later process on that --cache root, carrying
/// whatever the fast path got wrong. The cold first cycle still publishes, as the control.
/// ServerIncrementalSignatureChangeTests covers --server and the AL-observable consequence.
/// Spawns the real runner; needs the BC artifact cache.
/// </summary>
public class WatchFastPathCachePublishTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RecordTriggerXRec"));

    private static int CachedAssemblies(string cacheDir)
        => Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir, "*.dll", SearchOption.AllDirectories).Length : 0;

    [SkippableFact]
    public async Task Watch_FastPathCycle_PublishesNothingToTheAlOutputCache()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = TestScratch.Dir("al-runner-watch-fastpath-cache");
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var tablePath = Path.Combine(bundle, "XRecProbe.Table.al");
        var cacheDir = Path.Combine(Path.GetDirectoryName(bundle)!, Path.GetFileName(bundle) + "-cache");

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{bundle}\" --watch --cache \"{cacheDir}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r, OutputStream stream) => Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null) lock (lines) lines.Add(new CapturedLine(stream, l));
        });
        Pump(p.StandardOutput, OutputStream.Stdout);
        Pump(p.StandardError, OutputStream.Stderr);

        string DumpAll() { lock (lines) return string.Join("\n", lines.Select((l, i) => $"[{i}][{l.Stream}] {l.Text}")); }

        async Task<int> WaitForMarkerAfter(int fromIndex, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(lines, WatchOutputSlicing.WaitingForSourceMarker, fromIndex);
                if (found.Count > 0) return found[0];
                if (p.HasExited)
                {
                    await Task.Delay(500);
                    throw new TimeoutException($"watch exited early (exit {p.ExitCode}).\n{DumpAll()}");
                }
                await Task.Delay(200);
            }
            throw new TimeoutException($"watch marker not seen.\n{DumpAll()}");
        }

        try
        {
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var afterCold = CachedAssemblies(cacheDir);
            Assert.True(afterCold > 0, $"the cold cycle published nothing to {cacheDir}; the control failed.\n{DumpAll()}");

            WatchEdit.Replace(tablePath, await File.ReadAllTextAsync(tablePath) + "\n// touched\n");
            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            // The FULL REBUILD line goes to stderr and can land after the marker (#2653); give it time.
            await Task.Delay(1500);
            string cycle2;
            lock (lines) cycle2 = WatchOutputSlicing.MergedJoin(lines, m1 + 1, lines.Count);
            Assert.False(cycle2.Contains("FULL REBUILD"),
                $"the comment edit fell back to a full rebuild, so this cycle says nothing about the fast path.\n{DumpAll()}");

            Assert.True(CachedAssemblies(cacheDir) == afterCold,
                $"the fast-path cycle published to the AL-output cache ({afterCold} -> {CachedAssemblies(cacheDir)} "
                + $"assemblies); only a full compile may (#5092).\n{DumpAll()}");
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
