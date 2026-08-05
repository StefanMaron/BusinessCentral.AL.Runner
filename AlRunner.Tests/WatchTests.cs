using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Proves `--watch` re-runs IN-PROCESS and picks up a source edit on the next
/// cycle (the same-bundle reload working inside the resident watch process — the
/// gap that previously forced watch to spawn a child). We edit a table trigger so
/// the result flips PASS→FAIL on the second cycle; stale in-process state would
/// keep it passing.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
[Collection("server-serial")]
public class WatchTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RecordTriggerXRec"));

    private static bool ArtifactsPresent()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        return !string.IsNullOrEmpty(home) && Directory.Exists(Path.Combine(home, ".bcartifacts.cache", "sandbox"));
    }

    private static string CurrentFramework()
    {
        var v = Environment.Version;
        return $"net{v.Major}.{v.Minor}";
    }

    [Fact]
    public async Task Watch_PicksUpEdit_InProcess_OnNextCycle()
    {
        if (!ArtifactsPresent()) { Console.Error.WriteLine("[skip] BC artifact cache not present"); return; }

        var bundle = Path.Combine(Path.GetTempPath(), "al-runner-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var tablePath = Path.Combine(bundle, "XRecProbe.Table.al");
        var cacheDir = Path.Combine(bundle, ".cache");

        var lines = new List<string>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{bundle}\" --watch --cache \"{cacheDir}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.Environment["BCCOMPILER_TIMING"] = "1"; // emit GetSharedReferences timing lines
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r) => Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null) lock (lines) lines.Add(l);
        });
        Pump(p.StandardOutput);
        Pump(p.StandardError);

        async Task<int> WaitForMarkerAfter(int fromIndex, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (lines)
                    for (int i = fromIndex; i < lines.Count; i++)
                        if (lines[i].Contains("[watch] waiting for AL source")) return i;
                await Task.Delay(200);
            }
            string dump; lock (lines) dump = string.Join("\n", lines.TakeLast(40));
            throw new TimeoutException($"watch marker not seen.\n--- last output ---\n{dump}");
        }

        string Segment(int from, int to)
        {
            lock (lines) return string.Join("\n", lines.GetRange(from, Math.Max(0, to - from)));
        }

        try
        {
            // Cycle 1 (cold): the fixture test passes (Counter 0 -> 1, asserts '1').
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            Assert.Contains("PASS", cycle1);
            Assert.Contains("Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage", cycle1);
            Assert.DoesNotContain("FAIL  Codeunit", cycle1);

            // Edit ONLY the table trigger (+1 -> +9). The test still asserts '1', so
            // the next cycle MUST now FAIL — proving the edited table was reloaded
            // in-process (stale state would keep it passing).
            var table = await File.ReadAllTextAsync(tablePath);
            var edited = table.Replace("xRec.\"Counter\" + 1", "xRec.\"Counter\" + 9");
            Assert.NotEqual(table, edited);
            await File.WriteAllTextAsync(tablePath, edited);

            // Cycle 2 (warm, after the edit).
            //
            // This budget is only "did the cycle finish at all" — it is NOT the warm-vs-cold
            // claim, which the GetSharedReferences timing assertion below makes on its own. So
            // it should be generous: at 60s this was the single flaky test in the suite, passing
            // in 57s when run alone and timing out when the full 193-test suite had just driven
            // the machine through several BC engine boots. A cycle that has genuinely gone cold
            // still fails loudly on the <5s assertion below, so a longer wait here costs nothing
            // and removes a false red that trains people to re-run and shrug.
            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);
            Assert.Contains("FAIL", cycle2);
            Assert.Contains("Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage", cycle2);
            // The dependency loader stayed warm in-process across the edit: the
            // re-emit's symbol load is near-instant, not a cold ~40s reload.
            //
            // Assert the MAGNITUDE, not the exact string. This used to be
            // Assert.Contains("0ms"), which demanded the step round to exactly zero
            // milliseconds — on a Release build or a loaded machine it reports "1ms"
            // and the test failed despite the warm path working perfectly. The real
            // claim is warm (milliseconds) vs cold (tens of seconds), so a generous
            // ceiling still fails loudly if the loader ever goes cold here.
            Assert.Contains("GetSharedReferences", cycle2);
            // The label carries a parenthetical, e.g.
            //   [emit-timing] GetSharedReferences (5 specs): 787ms
            var timing = System.Text.RegularExpressions.Regex.Match(
                cycle2, @"GetSharedReferences[^:]*:\s*(\d+)ms");
            Assert.True(timing.Success,
                $"expected an '[emit-timing] GetSharedReferences: <n>ms' line in cycle 2, got:\n{cycle2}");
            var elapsedMs = int.Parse(timing.Groups[1].Value);
            Assert.True(elapsedMs < 5_000,
                $"warm in-process symbol load took {elapsedMs}ms — a warm re-emit must not pay " +
                "the cold ~40s dependency reload. The loader did not stay warm across the edit.");
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
