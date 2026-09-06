// WatchQuerySymbolsReloadTests — the regression guard for #2939's fix.
//
// ── WHY A SUBPROCESS --watch TEST AND NOT AN IN-PROCESS ONE ─────────────────────────────────
//
// #2939 is "RecordPatches._bcQuerySymbolJsonPaths accumulates across bundles and nothing clears
// it", and BundleQuerySymbolsResetTests proves the clear in-process. Clearing it ALONE is a
// --watch regression, and no in-process test can see that, because the thing that breaks lives
// on the other side of the boundary: BcCompiler.TryEmitIncremental's RAD fast paths hand a
// result back WITHOUT calling Emit, and Emit is the only caller of
// EmitAndRegisterBundleQuerySymbols. So from --watch cycle 2 onward nothing re-registers the
// bundle's query symbols, and before #2939 the ONLY reason queries kept working was that the
// list was never cleared — an accidental cache doing the job
// BcCompiler._radPageMetadataByModule does deliberately for page/xmlport metadata (#2593/#2654).
//
// Measured on tests/runner-extras/query-join-aggregation-oos, four --watch cycles, same machine:
//
//   unmodified main                       4 PASS on every cycle   (the accumulating list masks it)
//   clear only, no replay                 4 PASS cycle 1, 4 FAIL cycles 2-4
//   clear + _radQuerySymbolsPathByModule   4 PASS on every cycle
//
// The middle row is what this test exists to make impossible to ship again. It is a wrong
// NUMBER read out of a real row rather than a crash — the column ids in that file go verbatim
// to NavQuery.GetColumnValueSafe — so nothing else in the suite would have gone red.
//
// This asserts the runner's own behaviour across ITS OWN reload boundary, which is why it is a
// runner test and not a corpus one: real BC has no --watch, no RAD baseline and no
// SymbolReference.json sidecar. What the query COMPUTES (10 + 32 = 42, grouped into two
// customers) is ordinary BC behaviour, and it is asserted here only as the observable that
// discriminates a correctly-resolved column id from a stale one.
//
// Spawns the real runner; needs the BC artifact cache. Skips when absent, exactly like
// WatchPageMetadataReloadTests, whose harness shape this follows.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public class WatchQuerySymbolsReloadTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "WatchQuerySymbolsReload"));

    private const string TestName = "QueryReadsItsAggregatedColumnValues";
    private const string TestLabel = "Codeunit70623." + TestName;

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
    }

    // Same helper and conditional shape as WatchPageMetadataReloadTests.ExtraPackageCacheArgs:
    // CI's unit-test step provisions ~/.al-runner/platform-apps and passes it to nothing by
    // default, so a spawned subprocess has to be told about it explicitly.
    private static string[] ExtraPackageCacheArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    [SkippableFact]
    public async Task Watch_QueryStillResolvesItsOwnColumnIds_OnLaterCycles()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = TestScratch.Dir("al-runner-watch-querysymbols");
        CopyDir(FixtureRoot, bundle);
        var testsPath = Path.Combine(bundle, "WqsTests.Codeunit.al");
        // The AL-output cache goes in a private directory OUTSIDE the repository: the shared
        // ~/.cache/al-runner root is not keyed on the runner binary, so a concurrent run's
        // payload could make this measure something other than this build.
        var cacheDir = TestScratch.Dir("al-runner-watch-querysymbols-cache");

        var lines = new List<CapturedLine>();
        var argsBuilder = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg
            + $" \"{bundle}\" --watch --cache \"{cacheDir}\"");
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
                    await Task.Delay(500);
                    throw new TimeoutException(
                        $"watch marker not seen — subprocess exited early ({ProcessLiveness()}).\n"
                        + $"--- full subprocess output ---\n{DumpAll()}");
                }
                await Task.Delay(200);
            }
            if (p.HasExited) await Task.Delay(500);
            throw new TimeoutException(
                $"watch marker not seen. {ProcessLiveness()}\n--- full subprocess output ---\n{DumpAll()}");
        }

        string Segment(int from, int to) { lock (lines) return WatchOutputSlicing.MergedJoin(lines, from, to); }

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
            // Cycle 1 is always a full rebuild — there is no RAD baseline to diff against yet —
            // so it exercises the ordinary Emit path and is the control: if it does not pass,
            // nothing after it is a statement about the reload.
            int marker = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(300));
            CheckCycle("cycle 1 (cold, full rebuild)", () =>
            {
                var cycle = Segment(0, marker);
                Assert.Contains($"PASS  {TestLabel}", cycle);
            });

            // Three more cycles. More than one, because how soon the RAD fast path first engages
            // depends on when a clean baseline was recorded — with the AL-output cache warm the
            // first incremental cycle was observed as late as cycle 4. Every cycle after the
            // first must still resolve the query, so asserting all of them needs no assumption
            // about which one is the first incremental one.
            for (int cycle = 2; cycle <= 4; cycle++)
            {
                // Comment-only edit to the TEST codeunit. WqsSum.Query.al is never touched, so
                // the query is an object the app still declares and did not change this cycle —
                // the case a RAD delta's own Emit does not cover.
                var src = await File.ReadAllTextAsync(testsPath);
                var edited = src.Replace($"// EDIT-MARKER: {cycle - 1}", $"// EDIT-MARKER: {cycle}");
                Assert.NotEqual(src, edited);
                await File.WriteAllTextAsync(testsPath, edited);

                int next = await WaitForMarkerAfter(marker + 1, TimeSpan.FromSeconds(300));
                var window = Segment(marker + 1, next);
                marker = next;

                CheckCycle($"cycle {cycle} (warm)", () =>
                {
                    // Asserted rather than assumed: a cycle in which the test did not run at all
                    // would satisfy a DoesNotContain("FAIL") check trivially.
                    Assert.Contains(TestLabel, window);
                    Assert.DoesNotContain($"FAIL  {TestLabel}", window);
                    Assert.Contains($"PASS  {TestLabel}", window);
                });
            }
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
