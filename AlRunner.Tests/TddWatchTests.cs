using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2002 (follow-up to #1997): --tdd must work together with --watch, not be
/// rejected outright. This is the proving test the issue's own acceptance criteria
/// describe: cycle 1 (a test calls a not-yet-implemented procedure) reports the
/// synthetic FAILED test #1997 already builds for a one-shot --tdd run; a LATER
/// cycle, after the missing procedure is implemented and the file saved — WITHOUT
/// restarting the watch process — reports the same test PASSED.
///
/// The mechanism that makes this correct is entirely pre-existing (see Program.cs's
/// updated comment at the former "if (tddMode && watchMode)" rejection site, and
/// BcCompiler.Incremental.cs's TryEmitIncremental doc comment): BcCompiler.Emit only
/// records a RAD baseline on a CLEAN compile (nothing excluded). A --tdd cycle that
/// excludes an object for a missing symbol therefore never records one, which forces
/// every cycle up to and including the one that resolves the missing symbol through
/// TryEmitIncremental's "no incremental baseline yet" fallback into the SAME full
/// Emit() retry loop a one-shot --tdd run already uses. Nothing had to learn to carry
/// TDD diagnostics through BC's CreateForRad — this test proves that mechanism holds
/// end to end through a real --watch subprocess, not just by reading the code.
///
/// Also proves the corollary from the issue's requirement 3: the console must say
/// WHY the cycle was slower under --tdd, not just that it was — the fallback reason
/// on cycle 2 must be --tdd-specific, not the generic "previous cycle fell back" text
/// a non-tdd watch session would show for the exact same "no baseline yet" shape.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// See DefineFlagIntegrationTests for why runner-subprocess tests used to be
/// [Collection("server-serial")] and no longer are — #1809.
/// </summary>
public class TddWatchTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureSrc = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TddWatch"));

    [SkippableFact]
    public async Task TddWatch_MissingSymbol_ReportsFailedThenPasses_WithoutRestart()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = Path.Combine(Path.GetTempPath(), "al-runner-tdd-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        foreach (var f in Directory.GetFiles(FixtureSrc))
            File.Copy(f, Path.Combine(bundle, Path.GetFileName(f)));
        var targetCuPath = Path.Combine(bundle, "TddWatchTargetCu.Codeunit.al");
        var cacheDir = Path.Combine(bundle, ".cache");

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{bundle}\" --tdd --watch --cache \"{cacheDir}\"",
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

        string ProcessLiveness() =>
            p.HasExited ? $"process alive=false exit={p.ExitCode}" : "process alive=true";
        string DumpTail() { lock (lines) return string.Join("\n", lines.TakeLast(60).Select(l => $"[{l.Stream}] {l.Text}")); }

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
                    throw new TimeoutException(
                        $"watch marker not seen — subprocess exited early ({ProcessLiveness()}).\n--- last output ---\n{DumpTail()}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500);
            throw new TimeoutException($"watch marker not seen. {ProcessLiveness()}\n--- last output ---\n{DumpTail()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        // Cross-stream diagnostics ("TDD-EXCLUDED", "FULL REBUILD") are written to
        // STDERR by an INDEPENDENT pump task from the one that positions the stdout
        // "waiting for source" markers — `lines`' overall order is pump-SCHEDULING
        // order, not cross-stream WRITE order (see WatchOutputSlicing.cs's header,
        // #1843). WatchTests.cs already hit this for its own stderr timing diagnostic
        // and works around it by polling for an ABSOLUTE occurrence on the unbounded
        // stderr stream instead of reading a stdout-bounded window snapshot — do the
        // same here rather than trusting Segment(...) to contain a stderr line just
        // because it printed between the same two stdout markers in wall-clock time.
        // This fixture is deliberately tiny (one codeunit + a placeholder) so BOTH
        // cycles are fast, which — unlike the larger, slower fixture
        // WatchFullRebuildReasonTests uses — leaves little natural separation between
        // a cycle's own stderr diagnostic and its neighbouring stdout markers.
        async Task WaitForStderrContains(string needle, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                string text; lock (lines) text = WatchOutputSlicing.StderrText(lines);
                if (text.Contains(needle, StringComparison.Ordinal)) return;
                if (p.HasExited)
                {
                    await Task.Delay(500);
                    lock (lines) text = WatchOutputSlicing.StderrText(lines);
                    throw new TimeoutException(
                        $"stderr never contained \"{needle}\" — subprocess exited early " +
                        $"({ProcessLiveness()}).\n--- stderr ---\n{text}");
                }
                await Task.Delay(200);
            }
            string dump; lock (lines) dump = WatchOutputSlicing.StderrText(lines);
            throw new TimeoutException(
                $"stderr never contained \"{needle}\" after {timeout.TotalSeconds}s. " +
                $"{ProcessLiveness()}\n--- stderr ---\n{dump}");
        }

        try
        {
            // Cycle 1 (cold, process start): the test calls DoubleIt, which "Tdd Watch
            // Target Cu" does not declare yet — this must report FAILED, naming DoubleIt,
            // via the SAME TDD-EXCLUDED synthetic-test mechanism a one-shot --tdd run
            // uses (#1997), not a generic EMIT-EXCLUDED compile failure. The FAIL line
            // and the missing-symbol name both come from Reporter.PrintPerTest on
            // STDOUT (the synthetic TestResult's own Message), so they are safe to
            // assert on the stdout-bounded window; "TDD-EXCLUDED" itself is a stderr
            // diagnostic, checked separately below via WaitForStderrContains.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            Assert.Contains("FAIL ", cycle1);
            Assert.Contains("MissingProcedure_ReportsFailedThenPasses", cycle1);
            Assert.Contains("DoubleIt", cycle1);

            await WaitForStderrContains("TDD-EXCLUDED", TimeSpan.FromSeconds(10));
            // Cycle 0 always falls back to a full compile (no baseline exists yet for
            // ANY reason, tdd or not), so "FULL REBUILD" must NOT appear for it — see
            // WatchFullRebuildReasonTests for the same "cycle 0 is quiet" claim.
            string stderrAfterCycle1; lock (lines) stderrAfterCycle1 = WatchOutputSlicing.StderrText(lines);
            Assert.DoesNotContain("FULL REBUILD", stderrAfterCycle1);

            // Implement DoubleIt IN PLACE — the same file, same process, no restart.
            // Insert the new procedure just before the codeunit's final closing brace
            // (rather than a string-replace over the existing procedure body, which
            // would be fragile against line-ending differences on disk).
            var original = await File.ReadAllTextAsync(targetCuPath);
            var lastBrace = original.LastIndexOf('}');
            Assert.True(lastBrace >= 0, $"fixture has no closing brace to insert before:\n{original}");
            var edited = original[..lastBrace]
                + "\n    procedure DoubleIt(X: Integer): Integer\n    begin\n        exit(X * 2);\n    end;\n"
                + original[lastBrace..];
            Assert.NotEqual(original, edited);
            await File.WriteAllTextAsync(targetCuPath, edited);

            // Cycle 2: the module now compiles clean (nothing excluded), so the test
            // actually RUNS this time and must report PASSED — proving the synthetic
            // failure from cycle 1 was not some permanently-cached verdict that a
            // real fix can never overturn.
            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);
            Assert.Contains("PASS", cycle2);
            Assert.Contains("MissingProcedure_ReportsFailedThenPasses", cycle2);
            Assert.DoesNotContain("FAIL ", cycle2);

            // No baseline was ever recorded after cycle 1 (an excluded object skips
            // RecordIncrementalBaseline entirely — see BcCompiler.cs), so this cycle
            // MUST fall back to a full rebuild too, and — since watchCycleIndex > 0
            // here — that fallback IS reported, with the --tdd-specific reason text
            // (not the generic one a non-tdd watch session would show for the exact
            // same "no baseline yet" shape). This is requirement 3's "tell the user
            // WHY" claim, proven end to end. Both are stderr diagnostics — checked via
            // WaitForStderrContains, not the stdout-bounded cycle2 window, for the
            // same cross-stream-ordering reason as cycle 1's TDD-EXCLUDED check above.
            await WaitForStderrContains("FULL REBUILD", TimeSpan.FromSeconds(10));
            await WaitForStderrContains("--tdd reported", TimeSpan.FromSeconds(10));
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
