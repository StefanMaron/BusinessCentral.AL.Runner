using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2002 (follow-up to #1997): --tdd must work together with --watch, not be
/// rejected outright. This is the proving test the issue's own acceptance criteria
/// describe: cycle 1 (a test calls a not-yet-implemented procedure) reports FAILED;
/// a LATER cycle, after the missing procedure is implemented and the file saved —
/// WITHOUT restarting the watch process — reports the same test PASSED.
///
/// Written and first verified against #2000's refuse-only --tdd (every missing
/// symbol reported failed, nothing generated). #2005 landed member GENERATION while
/// this PR was in flight (--tdd now infers and generates a stub for a resolvable
/// missing symbol, per #2001) — rebasing onto it changed the fixture's behaviour, so
/// this file was reworked. Two things are worth recording because they are not what
/// the PR's own original design comment predicted:
///
/// 1. DoubleIt (this fixture's originally-missing procedure) is now a RESOLVABLE case
///    per #2001's inference rules: --tdd generates a stub for it rather than excluding
///    it. Cycle 1 therefore reports FAILED via Program.cs's OverrideTddDependentResults
///    ("this test depends on N generated member(s)..."), not TddSupport's refuse-path
///    message — and, because the module then compiles CLEAN (nothing excluded),
///    BcCompiler.Emit's RecordIncrementalBaseline (gated on `excludedObjects.Count ==
///    0`) actually records a baseline after cycle 1. That's fine for the underlying
///    safety argument (a baseline recorded from a generated compile is superseded
///    object-by-object the moment the real file content differs — see
///    BcCompiler.Incremental.cs's own "unmodified caller" guarantee), but it means a
///    single-scenario version of this test can no longer reliably demonstrate the
///    --tdd-specific "no incremental baseline yet" fallback-reason text this PR adds:
///    with a baseline present, TryEmitIncremental actually ATTEMPTS BC's CreateForRad
///    on cycle 2 — and that attempt independently fails for a reason unrelated to
///    --tdd entirely (any bundle with real MS-app dependencies, tdd or not, currently
///    falls back here — see issue #2009, filed from this investigation).
/// 2. TddWatchRefusedTests.Codeunit.al (added alongside the original fixture) closes
///    that gap cleanly: its bare-statement call to DoThing has no type anchor, so
///    --tdd's generation REFUSES it (TddGeneration.cs) rather than guessing, and that
///    object is excluded from EVERY compile — DoThing is deliberately never
///    implemented anywhere in this fixture. A permanently-excluded object keeps the
///    WHOLE MODULE's `excludedObjects.Count > 0` on every single cycle, so
///    RecordIncrementalBaseline never runs at all, for this bundle, ever — meaning
///    TryEmitIncremental short-circuits at its very first check ("no incremental
///    baseline yet") on EVERY cycle, including cycle 2, without ever reaching
///    CreateForRad and without the #2009 confound. That is what lets this test assert
///    the --tdd-specific fallback-reason text this PR actually adds, cleanly.
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

        // Cross-stream diagnostics ("FULL REBUILD") are written to STDERR by an
        // INDEPENDENT pump task from the one that positions the stdout "waiting for
        // source" markers — `lines`' overall order is pump-SCHEDULING order, not
        // cross-stream WRITE order (see WatchOutputSlicing.cs's header, #1843).
        // WatchTests.cs already hit this for its own stderr timing diagnostic and
        // works around it by polling for an ABSOLUTE occurrence on the unbounded
        // stderr stream instead of reading a stdout-bounded window snapshot — do the
        // same here rather than trusting Segment(...) to contain a stderr line just
        // because it printed between the same two stdout markers in wall-clock time.
        // This fixture's cycles are fast (small module), which — unlike the larger
        // fixture WatchFullRebuildReasonTests uses — leaves little natural
        // separation between a cycle's own stderr diagnostic and its neighbouring
        // stdout markers.
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
            // Cycle 1 (cold, process start): DoubleIt is resolvable, so --tdd
            // generates a stub for it — the test compiles and RUNS against that
            // stub, force-reported FAILED naming the generated member.
            // BareStatementCall_RefusesNotGuesses (a SIBLING object, DoThing) is not
            // resolvable at all — --tdd refuses to guess, that object is excluded,
            // and its test is reported FAILED via TddSupport's original (#2000)
            // refuse-path message. Both come from Reporter.PrintPerTest on stdout.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            Assert.Contains("FAIL ", cycle1);
            Assert.Contains("MissingProcedure_ReportsFailedThenPasses", cycle1);
            Assert.Contains("DoubleIt", cycle1);
            Assert.Contains("depends on", cycle1); // OverrideTddDependentResults' message shape
            Assert.Contains("BareStatementCall_RefusesNotGuesses", cycle1);
            Assert.Contains("DoThing", cycle1);
            Assert.Contains("did not compile", cycle1); // TddSupport.BuildFailedTests' message shape

            // Implement DoubleIt IN PLACE — the same file, same process, no restart.
            // Insert the new procedure just before the codeunit's final closing brace
            // (rather than a string-replace over the existing procedure body, which
            // would be fragile against line-ending differences on disk). DoThing is
            // deliberately left unimplemented — see this class's doc comment for why
            // that permanent exclusion is exactly what this test needs.
            var original = await File.ReadAllTextAsync(targetCuPath);
            var lastBrace = original.LastIndexOf('}');
            Assert.True(lastBrace >= 0, $"fixture has no closing brace to insert before:\n{original}");
            var edited = original[..lastBrace]
                + "\n    procedure DoubleIt(X: Integer): Integer\n    begin\n        exit(X * 2);\n    end;\n"
                + original[lastBrace..];
            Assert.NotEqual(original, edited);
            await File.WriteAllTextAsync(targetCuPath, edited);

            // Cycle 2: DoubleIt's test now compiles against the REAL implementation
            // (no generated member involved at all) and must report PASSED — proving
            // the FAILED verdict from cycle 1 was not some permanently-cached verdict
            // a real fix can never overturn. DoThing's test is STILL excluded (never
            // implemented) and must still report FAILED the same way.
            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);
            Assert.Contains("PASS", cycle2);
            Assert.Contains("MissingProcedure_ReportsFailedThenPasses", cycle2);
            Assert.Contains("BareStatementCall_RefusesNotGuesses", cycle2);
            Assert.Contains("DoThing", cycle2);
            Assert.Contains("did not compile", cycle2);

            // Requirement 3's "tell the user why" claim, proven end to end: this
            // cycle falls back to a full rebuild, and — since watchCycleIndex > 0
            // here — that fallback IS reported, with the --tdd-specific reason text
            // this PR adds (not the generic one a non-tdd watch session would show
            // for the exact same "no baseline yet" shape). Reachable cleanly here
            // because DoThing's permanent exclusion keeps a baseline from EVER being
            // recorded for this bundle — see this class's doc comment. Checked via
            // the unbounded stderr poll above, not the stdout-bounded cycle2 window,
            // for the same cross-stream-ordering reason as every stderr assertion in
            // this file.
            await WaitForStderrContains("FULL REBUILD", TimeSpan.FromSeconds(10));
            await WaitForStderrContains("--tdd reported", TimeSpan.FromSeconds(10));
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
