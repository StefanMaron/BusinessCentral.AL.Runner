// WatchDependentBundleInventoryTests — the regression guard for #2684.
//
// THE DEFECT
// ----------
// `BcRuntime.ResetForNewBundleReload()` clears the object-metadata registries
// (AlObjectMetadataRegistry, AlPageMetadataRegistry, AlReportMetadataRegistry, …). Under
// `--watch` it used to be called INSIDE `foreach (var bundle in bundles)`, guarded by a comment
// claiming it was a "No-op on the first iteration (caches already empty)". That premise holds
// for ONE bundle and is false for two: by the time the second bundle's iteration reaches it, the
// first bundle has already registered its objects, and the reset erases them. A bundle that
// DEPENDS on an earlier bundle then cannot see its dependency's objects. The one-shot CLI path
// never called the reset at all, which is why the two modes disagreed on identical source.
//
// Measured on the fixture this test drives (`app-group-visibility-a` + `-c`, 54 tests):
//
//     plain            54 pass / 0 fail
//     --watch cycle 1  46 pass / 8 fail      <- all 8 are C asserting A's objects are visible
//
// WHY THIS TEST DRIVES --watch AND NOT THE PLAIN PATH
// ---------------------------------------------------
// A plain-mode test CANNOT fail for this defect, because the plain path never calls the reset.
// Asserting the same thing one-shot would be the vacuous shape tdd.md forbids: green before the
// fix and green after. So this spawns a real `--watch` subprocess and reads its cycle output.
//
// WHY BOTH CYCLES ARE ASSERTED, NOT JUST CYCLE 1
// ----------------------------------------------
// The fix moves the reset to once per CYCLE, and that move has its own edge, which this test is
// also the guard for. `RecordPatches._lazyMetaQueryByGetById` and `_realMetaQueryCache` memoize a
// NEGATIVE answer derived from `BcRuntime.FindQueryType(id)` — a per-bundle answer. From cycle 2
// on, the accumulated known-query id set already contains ids only a LATER bundle declares, so
// the EARLIER bundle's run asks about them, gets null, and memoizes it; the later bundle is then
// served "no such query" for its OWN query. Measured while developing the fix: hoisting the
// reset alone gave cycle 1 = 54/0 but cycles 2 and 3 = 53/1, the one failure being
// `QueryMetadata_OwnQuery_IsListed` — C's own query, not a dependency one. That is why
// `RecordPatches.ResetNegativeQueryMemosForNewBundle()` stays per BUNDLE in the loop.
//
// So: cycle 1 pins the original defect, cycle 2 pins the edge the fix introduced. Asserting only
// cycle 1 would have let that regression ship.
//
// WHAT WOULD MAKE THIS FAIL
// -------------------------
//   * The per-cycle reset moves back inside the bundle loop — cycle 1 loses A's registrations.
//   * `ResetNegativeQueryMemosForNewBundle` stops being called per bundle, or is widened to
//     clear registrations a later bundle needs — cycle 2 loses C's own query row.
//   * A watch cycle stops re-running every bundle (surfaces as the cycle-marker timeout).
using System.Diagnostics;
using Xunit;

namespace AlRunner.Tests;

public class WatchDependentBundleInventoryTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>Bundle A: no dependencies, declares its own table/page/report/query/codeunit.</summary>
    private static string DependencyBundle =>
        Path.Combine(RepoRoot, "tests", "runner-extras", "app-group-visibility-a");

    /// <summary>Bundle C: declares a dependency on A, and asserts A's objects ARE in its
    /// inventory while an unrelated sibling group's are not.</summary>
    private static string DependentBundle =>
        Path.Combine(RepoRoot, "tests", "runner-extras", "app-group-visibility-c");

    /// <summary>
    /// Under <c>--watch</c>, a bundle that declares a dependency on an EARLIER bundle in the same
    /// run must see that dependency's objects — on the first cycle and on every later one — and
    /// must answer exactly as the equivalent one-shot CLI run does.
    /// </summary>
    [SkippableFact]
    public async Task WatchCycles_ADependentBundle_StillSeesTheEarlierBundlesObjects()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-watch-dep-inventory");
        Directory.CreateDirectory(root);
        // Outside the repository: a --cache inside a worktree has faked a whole-bundle install
        // failure before.
        var cacheDir = Path.Combine(root, "cache");
        Directory.CreateDirectory(cacheDir);

        var lines = new List<CapturedLine>();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
                + $" \"{DependencyBundle}\" \"{DependentBundle}\""
                + $" --cache \"{cacheDir}\" --package-cache \"{TestArtifacts.PlatformAppsDir()}\" --watch",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        void Pump(StreamReader r, OutputStream stream) => _ = Task.Run(async () =>
        {
            string? l;
            while ((l = await r.ReadLineAsync()) != null)
                lock (lines) lines.Add(new CapturedLine(stream, l));
        });
        Pump(p.StandardOutput, OutputStream.Stdout);
        Pump(p.StandardError, OutputStream.Stderr);

        string DumpTail() { lock (lines) return string.Join("\n", lines.TakeLast(100).Select(l => $"[{l.Stream}] {l.Text}")); }

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
                    await Task.Delay(500);
                    throw new TimeoutException(
                        $"watch marker not seen — subprocess exited early (exit={p.ExitCode}).\n"
                        + $"--- last output ---\n{DumpTail()}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500);
            throw new TimeoutException($"watch marker not seen.\n--- last output ---\n{DumpTail()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

        void AssertCycleHealthy(string cycle, string label)
        {
            // Named individually rather than left to the pass/fail bar, because these EIGHT are
            // the defect: every one of them is bundle C asserting that an object of its declared
            // dependency A is listed. A regression that reddens the cycle some other way must not
            // be able to satisfy this assertion, and a regression that drops exactly these must
            // not be able to hide behind a total.
            foreach (var crossDependencyTest in new[]
            {
                "AllObj_DependencyATable_IsListed",
                "AllObjWithCaption_DependencyATable_IsListed",
                "TableMetadata_DependencyATable_IsListed",
                "CodeunitMetadata_DependencyACodeunit_IsListed",
                "PageMetadata_DependencyAPage_IsListed",
                "ReportMetadata_DependencyAReport_IsListed",
                "ReportDataItems_DependencyAReport_IsListed",
                "QueryMetadata_DependencyAQuery_IsListed",
            })
                Assert.False(cycle.Contains($"FAIL  Codeunit62622.{crossDependencyTest}", StringComparison.Ordinal),
                    $"{label}: {crossDependencyTest} failed — the dependent bundle could not see its "
                    + "dependency's object. That is #2684: the per-bundle "
                    + "BcRuntime.ResetForNewBundleReload() erased the earlier bundle's registrations "
                    + "before this bundle ran.\n" + cycle);

            // The edge the fix introduced, asserted by name for the same reason: C's OWN query,
            // lost from cycle 2 on when a negative memo taken during A's run outlived that run.
            Assert.False(cycle.Contains("FAIL  Codeunit62622.QueryMetadata_OwnQuery_IsListed", StringComparison.Ordinal),
                $"{label}: the executing bundle's OWN query is missing from Query Metadata. A "
                + "negative FindQueryType answer memoized during an EARLIER bundle's run in this "
                + "cycle was served to this bundle — RecordPatches.ResetNegativeQueryMemosForNewBundle() "
                + "must run per BUNDLE (#2684).\n" + cycle);

            // The bar: a --watch cycle must answer as the equivalent one-shot CLI run does, which
            // for this fixture is 54/54.
            Assert.Contains("pass:        54", cycle, StringComparison.Ordinal);
            Assert.Contains("fail:        0", cycle, StringComparison.Ordinal);
        }

        try
        {
            // ── Cycle 1. The original defect: the reset fired between bundle A and bundle C
            // within this cycle, so C ran against an inventory A had been erased from.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(600));
            AssertCycleHealthy(Segment(0, m1), "cycle 1");

            // ── Cycle 2. Edit the DEPENDENT bundle and let it re-run. This is where the
            // negative-query-memo edge shows up: by now the known-query id set carries both
            // bundles' ids, so A's run asks about C's query before C has re-emitted it.
            var dependentSource = Path.Combine(DependentBundle, "AgvCTests.Codeunit.al");
            var original = File.ReadAllText(dependentSource);
            try
            {
                File.WriteAllText(dependentSource, "// #2684 watch-cycle-2 edit\n" + original);
                int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(600));
                AssertCycleHealthy(Segment(m1 + 1, m2), "cycle 2 (dependent bundle edited)");
            }
            finally
            {
                File.WriteAllText(dependentSource, original);
            }
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
