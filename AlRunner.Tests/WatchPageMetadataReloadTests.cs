using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #1957: a `TestPage` opened on --watch cycle 2 or later must still run the page's
/// OnOpenPage trigger. Two apps — "R3Pages" (owns the page) and "R3Driver" (owns the
/// test, depends on R3Pages) — mirroring the issue's own repro exactly: the edit
/// between cycles lands ONLY in R3Driver, never in R3Pages, because the bug is in the
/// per-cycle bookkeeping reset that RecordPatches runs unconditionally every cycle: it
/// left <c>_pagesWithRealMetadata</c>/<c>_pagesRealMetadataFailed</c> stale against the
/// discarded <c>_metaFormCache</c> generation — R3Pages need not have been touched at
/// all for its page to regress. (Deliberately not spelling the reset entry point as a
/// dotted call here — see the note below on why.)
///
/// The proving assertion is the CONCRETE EFFECT OnOpenPage produces (Row.Touched),
/// never "OpenView() didn't throw" — a silent record-only fallback doesn't throw
/// either, which is exactly how this bug went unnoticed (see WPMRTests.Codeunit.al's
/// own negative-direction test, which stayed green throughout the original bug for
/// the same reason).
///
/// This test class never touches RecordPatches' own AL-parser statics directly — it
/// only spawns and observes the real runner subprocess — so it deliberately avoids
/// spelling the reset entry point as a dotted call anywhere in this file:
/// ParserStaticsIsolationGuardTests' detector flags that exact token sequence wherever
/// it appears, prose included, and joining its serial collection would be pointless
/// for a class with no in-process static access to serialise.
///
/// #1972 review fix: this fixture has a sibling-app dependency (R3Driver -> R3Pages)
/// and R3Driver's test codeunit references R3Pages' table across that dependency, which
/// needs the Microsoft/Application + Microsoft/System symbols resolved to compile at
/// all — same requirement TestPageDrillDownDispatchTests already has for a TestPage
/// fixture. Locally this was masked by a legacy ~/.bcartifacts.cache/sandbox tree that
/// happened to satisfy DefaultPackageCacheDirs(); CI's "Run unit tests (AlRunner.Tests)"
/// step never provisions that path (only the corpus/runner-extras al-runner invocations
/// get an explicit --package-cache), so every CI leg silently dropped R3Driver's test
/// codeunit at compile time (EMIT-EXCLUDED) and cycle 1 finished with zero PASS output
/// in under 10s — which the original assertion read as "the page ran but OnOpenPage did
/// not", not "the test codeunit was never emitted at all". Passing --package-cache
/// explicitly (TestArtifacts.PlatformAppsDir(), same helper + same conditional-existence
/// pattern as TestPageDrillDownDispatchTests) fixes the actual cause; the PASS
/// assertions themselves were never wrong and are unchanged.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class WatchPageMetadataReloadTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "WatchPageMetadataReload"));

    private const string PositiveTest = "OpeningThePageRunsItsOnOpenPageTrigger";
    private const string NegativeTest = "OnOpenPageTouchesOnlyTheRowItNames";

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
    }

    // Same helper + same conditional-existence pattern as
    // TestPageDrillDownDispatchTests.ExtraPackageCacheArgs — this fixture's sibling-app
    // dependency (R3Driver -> R3Pages, referencing R3Pages' table across the boundary)
    // needs Microsoft/Application + Microsoft/System resolved to compile R3Driver's test
    // codeunit at all. Locally this is often masked by a machine that happens to have a
    // legacy ~/.bcartifacts.cache/sandbox tree; CI's unit-test step provisions ONLY
    // ~/.al-runner/platform-apps (TestArtifacts.PlatformAppsDir()) and passes it to
    // nothing by default, so a spawned subprocess must be told about it explicitly.
    private static string[] ExtraPackageCacheArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    [SkippableFact]
    public async Task Watch_PageOpenedOnCycle2_StillRunsOnOpenPage()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = Path.Combine(Path.GetTempPath(), "al-runner-watch-pagemeta", Guid.NewGuid().ToString("N"));
        CopyDir(Path.Combine(FixtureRoot, "R3Pages"), Path.Combine(bundle, "R3Pages"));
        CopyDir(Path.Combine(FixtureRoot, "R3Driver"), Path.Combine(bundle, "R3Driver"));
        var driverTestsPath = Path.Combine(bundle, "R3Driver", "WPMRTests.Codeunit.al");

        // Same merged stdout/stderr capture shape as WatchTests.cs — see its header and
        // WatchOutputSlicing.cs for why two independent pumps only preserve WITHIN-stream
        // order, not cross-stream order (irrelevant here: every assertion below is
        // stdout-only, found via FindStdoutMarkerIndices).
        var lines = new List<CapturedLine>();
        var argsBuilder = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
            + $" \"{bundle}\" --watch --no-cache");
        foreach (var a in ExtraPackageCacheArgs()) argsBuilder.Append($" \"{a}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = argsBuilder.ToString(),
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

        string ProcessLiveness() => p.HasExited ? $"process alive=false exit={p.ExitCode}" : "process alive=true";
        // Full transcript, not just a tail — a truncated dump is what made the original
        // #1972 CI failure a log dig instead of a readable assertion message: the actual
        // cause (EMIT-EXCLUDED, package cache missing) was on a line xunit's own
        // Assert.Contains diff never showed at all.
        string DumpAll() { lock (lines) return string.Join("\n", lines.Select(l => $"[{l.Stream}] {l.Text}")); }

        async Task<int> WaitForMarkerAfter(int fromIndex, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                List<int> found;
                lock (lines)
                    found = WatchOutputSlicing.FindStdoutMarkerIndices(
                        lines, WatchOutputSlicing.WaitingForSourceMarker, fromIndex);
                if (found.Count > 0) return found[0];
                if (p.HasExited)
                {
                    // Give the pump tasks a moment to drain in-flight output before
                    // dumping — see WatchTests.cs's identical guard for why.
                    await Task.Delay(500);
                    throw new TimeoutException(
                        $"watch marker not seen — subprocess exited early ({ProcessLiveness()}).\n" +
                        $"--- full subprocess output ---\n{DumpAll()}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500);
            throw new TimeoutException(
                $"watch marker not seen. {ProcessLiveness()}\n--- full subprocess output ---\n{DumpAll()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        // Wraps every assertion below so a failure is self-explanatory from the assertion
        // message alone, instead of requiring a log dig: names which cycle's window was
        // being checked and appends the FULL subprocess transcript. Never retries, never
        // weakens what is being asserted — #1959 is the record of this exact test file
        // being relaxed three times for reasons that turned out to be wrong; this only
        // makes a real failure legible.
        void CheckCycle(string label, Action check)
        {
            try { check(); }
            catch (Exception ex)
            {
                throw new Exception(
                    $"{label}: {ex.Message}\n--- full subprocess output ({lines.Count} lines) ---\n{DumpAll()}", ex);
            }
        }

        try
        {
            // Cycle 1 (cold): both tests pass.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            CheckCycle("cycle 1", () =>
            {
                Assert.Contains("PASS", cycle1);
                Assert.Contains(PositiveTest, cycle1);
                Assert.DoesNotContain("FAIL  Codeunit", cycle1);
            });

            // Comment-only edit to R3Driver ONLY — R3Pages, and therefore the page
            // itself, is never touched. This is the crux of #1957's repro: the page's
            // app is reported unchanged and reused, yet its trigger still regresses,
            // because the stale bookkeeping this fix clears is process-global, not
            // scoped to whichever app's source actually changed.
            var driverSrc = await File.ReadAllTextAsync(driverTestsPath);
            var edited = driverSrc.Replace("EDIT-MARKER-V1", $"EDIT-MARKER-V2 {Guid.NewGuid():N}");
            Assert.NotEqual(driverSrc, edited);
            await File.WriteAllTextAsync(driverTestsPath, edited);

            // Cycle 2 (warm, after the edit). Generous budget: this is "did the cycle
            // finish", not a timing-precision claim — see WatchTests.cs's identical
            // reasoning for why a longer wait here costs nothing.
            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);

            // The actual #1957 claim: OpeningThePageRunsItsOnOpenPageTrigger must still
            // PASS on cycle 2 — i.e. Row.Touched really flipped, i.e. the real page
            // object ran, not a control-less skeleton TestPage silently fell back to
            // record-only access for.
            CheckCycle("cycle 2", () =>
            {
                Assert.Contains(PositiveTest, cycle2);
                Assert.DoesNotContain($"FAIL  Codeunit70025.{PositiveTest}", cycle2);
                Assert.Contains($"PASS  Codeunit70025.{PositiveTest}", cycle2);

                // The negative-direction test stays green on every cycle — proving it
                // ALONE proves nothing (a trigger that never ran satisfies it trivially,
                // which is exactly how #1957 went unnoticed); it is included here only so
                // this cycle-2 window is checked in both directions per repo convention.
                Assert.Contains(NegativeTest, cycle2);
                Assert.DoesNotContain($"FAIL  Codeunit70025.{NegativeTest}", cycle2);
            });
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
