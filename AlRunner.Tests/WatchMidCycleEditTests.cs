using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4706: an edit saved while a <c>--watch</c> cycle is running must start another cycle.
/// The watch disposes its watchers for the length of a cycle, and inotify keeps no backlog,
/// so before the fix such an edit raised no event and the process idled on stale results.
///
/// Deterministic: <c>AL_RUNNER_TEST_BARRIER_DIR</c> holds the child process between the end
/// of a cycle and the re-arm, so the edit lands in exactly the window no watcher covers.
/// Spawns the real runner; needs the BC artifact cache.
/// </summary>
public class WatchMidCycleEditTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RecordTriggerXRec"));

    private const string TestName = "Insert_OnInsertReadsXRec_BuildsConcreteBeforeImage";

    [SkippableFact]
    public async Task Watch_EditSavedDuringCycle_StartsAnotherCycle()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = TestScratch.Dir("al-runner-watch-midcycle");
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var tablePath = Path.Combine(bundle, "XRecProbe.Table.al");
        // Inside the watched root on purpose: if a cycle wrote .al files into its cache, the
        // snapshot would see them and every cycle would start the next.
        var cacheDir = Path.Combine(bundle, ".cache");
        var barrierDir = TestScratch.Dir("al-runner-watch-midcycle-barrier");
        Directory.CreateDirectory(barrierDir);
        var release = Path.Combine(barrierDir, "release");

        var stdout = new List<string>();
        var stderr = new List<string>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{bundle}\" --watch --cache \"{cacheDir}\"",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        psi.Environment["AL_RUNNER_TEST_BARRIER_DIR"] = barrierDir;
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r, List<string> sink) => Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null) lock (sink) sink.Add(l);
        });
        Pump(p.StandardOutput, stdout);
        Pump(p.StandardError, stderr);

        string Dump()
        {
            lock (stdout) lock (stderr)
                return "--- stdout ---\n" + string.Join("\n", stdout.TakeLast(30))
                    + "\n--- stderr ---\n" + string.Join("\n", stderr.TakeLast(15))
                    + $"\nprocess alive={!p.HasExited}";
        }

        int SummaryCount() { lock (stdout) return stdout.Count(l => l.StartsWith("Tests:", StringComparison.Ordinal)); }

        async Task WaitForSummaries(int count, TimeSpan timeout, string what)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (SummaryCount() < count)
            {
                if (p.HasExited) { await Task.Delay(500); throw new Xunit.Sdk.XunitException($"runner exited before {what}.\n{Dump()}"); }
                if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException($"{what} did not happen within {timeout.TotalSeconds}s.\n{Dump()}");
                await Task.Delay(100);
            }
        }

        try
        {
            await WaitForSummaries(1, TimeSpan.FromSeconds(150), "cycle 1");
            lock (stdout) Assert.Contains("passed 1   failed 0", stdout.Last(l => l.StartsWith("Tests:")));

            // Cycle 1 has read its sources and the watch is not armed: edit now. +1 -> +9 makes
            // the fixture's test fail, so cycle 2 can only fail if it ran on the edited source.
            var table = await File.ReadAllTextAsync(tablePath);
            var edited = table.Replace("xRec.\"Counter\" + 1", "xRec.\"Counter\" + 9");
            Assert.NotEqual(table, edited);
            await File.WriteAllTextAsync(tablePath, edited);
            File.WriteAllText(release, "");

            await WaitForSummaries(2, TimeSpan.FromSeconds(120),
                "a second cycle after an edit saved while the watch was disarmed (#4706)");
            lock (stdout)
            {
                Assert.Contains("passed 0   failed 1", stdout.Last(l => l.StartsWith("Tests:")));
                Assert.Contains(stdout, l => l.StartsWith("FAIL") && l.Contains(TestName));
            }
            lock (stderr)
                Assert.Contains(stderr, l => l.Contains("XRecProbe.Table.al changed while the last cycle was running"));

            // Nothing changed during cycle 2, so the re-arm must not start a third.
            File.WriteAllText(release, "");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            bool Waiting() { lock (stdout) return stdout.Count(l => l.StartsWith("[watch] waiting for AL source changes")) >= 2; }
            while (!Waiting() && DateTime.UtcNow < deadline && !p.HasExited) await Task.Delay(100);
            Assert.True(Waiting(), $"cycle 2 never re-armed.\n{Dump()}");
            await Task.Delay(TimeSpan.FromMilliseconds(WatchSource.QuietMs * 4 + 1000));
            Assert.True(SummaryCount() == 2, $"an unchanged tree started another cycle.\n{Dump()}");
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
