using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #2593/#2579: a deleted page's metadata must stop answering after the next --watch
/// reload, while a page that survives unchanged must keep answering.
///
/// Root cause (verified empirically, not assumed — see the PR body for the full trace):
/// AlPageMetadataRegistry/AlXmlPortMetadataRegistry are populated ONLY as a side effect of
/// BC's own Compilation.Emit (BcCompiler.CaptureOutputter.AddApplicationObject). Before this
/// fix, BcRuntime.ResetForNewBundleReload() never cleared either registry — issue #2593's own
/// finding, AlPageMetadataRegistry.Clear()/AlXmlPortMetadataRegistry.Clear() existed with zero
/// callers anywhere in the repo. Naively adding those Clear() calls regressed #1957
/// (WatchPageMetadataReloadTests) — a page whose own .al file is UNCHANGED between two
/// --watch cycles never runs a full Emit again on the second cycle:
///   - "genuinely zero work" (BcCompiler.Incremental.cs's TryEmitIncremental): this app
///     group's files hash identical to the last cycle, so it replays the previous cycle's
///     BcEmitOutput verbatim WITHOUT calling Emit at all.
///   - a real RAD delta only runs Emit for the objects that actually changed this cycle —
///     an unrelated file being deleted does not re-register a page that was not touched.
/// So clearing without also restoring what an unaffected object's metadata used to be wipes
/// out surviving pages just as thoroughly as deleted ones. The fix threads a per-app-group
/// shadow snapshot through BcCompiler.Incremental.cs (see its own header comment on
/// _radPageMetadataByModule) that survives the reset and is replayed on every RAD fast path,
/// dropping only the ids an app genuinely no longer declares.
///
/// This fixture is a single, standalone app (deliberately NOT the R3Pages/R3Driver
/// cross-app dependency shape WatchPageMetadataReloadTests uses) so the claim here is
/// specifically about ONE app group's own RAD delta path — DependencyLoader's separate
/// AppId-reuse cache (the mechanism WatchPageMetadataReloadTests exercises) is a different
/// code path with its own fix (DependencyLoader.ReplayDependencyMetadataSidecars).
///
/// Observed via AL_RUNNER_TRACE_PAGE_METADATA=1's "[page-metadata] registered page N" stderr
/// line — a real AL-level "does the deleted page still answer" flow is not constructible: the
/// page's OWN CLR type is dropped from the compiled module the instant its .al file is
/// deleted (BcCompiler.Incremental.cs's own vacated-object handling — proven separately,
/// unconditionally, and not this fix's concern), so nothing in AL could ever again construct
/// an instance to query. The registry's own id-keyed lifecycle — "does a query for THIS id
/// still find something" — is the actual, narrower claim #2593 makes, and the trace line is
/// its direct, unmediated signal.
///
/// Spawns the real runner in --watch mode; needs the BC artifact cache. Skips (no-op) when
/// absent.
/// </summary>
public class WatchPageMetadataReloadDeleteTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "WatchPageMetadataReloadDelete"));

    private const int KeepPageId = 70201;
    private const int GonePageId = 70202;

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
    }

    // Same conditional-existence pattern as WatchPageMetadataReloadTests.ExtraPackageCacheArgs.
    private static string[] ExtraPackageCacheArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    [SkippableFact]
    public async Task Watch_PageDeletedBetweenCycles_StopsAnswering_SurvivingPageStillDoes()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = TestScratch.Dir("al-runner-watch-pagemeta-delete");
        CopyDir(FixtureRoot, bundle);
        var gonePagePath = Path.Combine(bundle, "RPRGone.Page.al");
        Assert.True(File.Exists(gonePagePath));

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
        // The claim this test proves lives on stderr (AL_RUNNER_TRACE_PAGE_METADATA), not
        // stdout — set on the child process only, same isolation shape WatchTests.cs uses
        // for its own env-var-gated diagnostics.
        psi.EnvironmentVariables["AL_RUNNER_TRACE_PAGE_METADATA"] = "1";
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
            // Cycle 1 (cold): the test passes, and both pages register their metadata — the
            // baseline every later assertion is relative to.
            int m1 = await WaitForMarkerAfter(0, TimeSpan.FromSeconds(150));
            var cycle1 = Segment(0, m1);
            CheckCycle("cycle 1", () =>
            {
                Assert.Contains("PASS  Codeunit70203.CycleCompletes", cycle1);
                Assert.Contains($"[page-metadata] registered page {KeepPageId}", cycle1);
                Assert.Contains($"[page-metadata] registered page {GonePageId}", cycle1);
            });

            // Delete "RPR Gone" between cycles. Nothing else in the fixture references it, so
            // the bundle still compiles — this is a genuine object removal, not a broken edit.
            File.Delete(gonePagePath);
            Assert.False(File.Exists(gonePagePath));

            int m2 = await WaitForMarkerAfter(m1 + 1, TimeSpan.FromSeconds(240));
            var cycle2 = Segment(m1 + 1, m2);

            CheckCycle("cycle 2", () =>
            {
                // The cycle actually ran and its (untouched, always-passing) test still passes —
                // this alone proves nothing about the metadata claim, but rules out "the whole
                // cycle silently failed" as an alternative explanation for either direction below.
                Assert.Contains("PASS  Codeunit70203.CycleCompletes", cycle2);

                // POSITIVE direction (surviving object): "RPR Keep" was not touched this cycle
                // — no full Emit ran for it (RAD delta only covers the objects that changed,
                // "RPR Gone" here) — yet its metadata is re-registered, because the shadow
                // snapshot this fix adds replays it. Without the fix this line is simply
                // absent: BcRuntime.ResetForNewBundleReload() cleared the registry and nothing
                // else touches "RPR Keep" this cycle.
                Assert.Contains($"[page-metadata] registered page {KeepPageId}", cycle2);

                // NEGATIVE direction (deleted object): "RPR Gone" no longer exists in the
                // bundle's own source, so nothing registers its id this cycle, and the shadow
                // snapshot's vacated-id removal (UpdateRadMetadataSnapshotDelta) makes sure the
                // replay does not resurrect it either. Before BcRuntime.ResetForNewBundleReload
                // cleared the registry at all (#2593's own finding), this id would have kept
                // answering forever, for the life of the --watch process.
                Assert.DoesNotContain($"[page-metadata] registered page {GonePageId}", cycle2);
            });
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }
}
