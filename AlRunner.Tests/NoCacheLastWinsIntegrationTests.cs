// NoCacheLastWinsIntegrationTests — end-to-end proof of issue #2555's fix, spawning the
// real runner as a subprocess (mirroring CacheRootsIsolationTests.cs's #1821 proof).
//
// #2555's bug: --no-cache only ever disabled the AL-output cache (Program.cs's
// alCacheDir). Every other cache resolved through CacheRoots.Resolve — this file uses
// ncl-cecil, which (per CacheRootsIsolationTests.cs's own note) is populated on EVERY
// invocation unconditionally, so a single bundle run is enough to exercise it without
// needing a dependency — stayed pointed at whatever --cache DIR the caller last gave,
// so `--cache DIR --no-cache` silently left the other caches warm under DIR while only
// al-out went cold. `--no-cache --cache DIR` happened to already work by accident (a
// later --cache unconditionally overwrote both alCacheDir and cacheRootOverride).
//
// CacheRootsDisableForRunTests.cs proves the CacheRoots-level mechanism directly (a
// throwaway directory really gets created, really receives files, and really gets
// deleted). This file proves the WIRING: that Program.cs's --no-cache parsing actually
// reaches CacheRoots.DisableForRun(), that --cache/--no-cache are last-wins against
// each other in BOTH orders, and that the redirect survives the real Cecil-rewrite
// re-exec (this bundle triggers one on a cold ncl-cecil cache, so a wiring bug that only
// set the throwaway root in the PARENT generation and let the re-exec'd CHILD mint a
// second one would show up here as a MISS against the parent's directory that this test
// cannot directly observe, but WOULD show up as cacheDirA gaining an entry it should
// not have — the redirect failing over to whatever cacheDirA still names once the
// parsing loop is re-run in the child with the forwarded --no-cache argument).
//
// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public class NoCacheLastWinsIntegrationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundleDir, string absentPackageCache, params string[] extraArgs)
        => RunRunnerCore(bundleDir, absentPackageCache, noCacheRoot: null, extraArgs);

    /// <param name="noCacheRoot">
    /// When non-null, handed to the child as <see cref="CacheRoots.NoCacheRootEnvVar"/> so a
    /// <c>--no-cache</c> run ADOPTS this exact directory instead of minting a GUID-named one
    /// (CacheRoots.DisableForRun's adopt branch). Set on the child's own ProcessStartInfo, never
    /// through Environment.SetEnvironmentVariable: the environment is process-global and
    /// Path.GetTempPath() re-reads it per call, so a test host mutating it redirects the temp
    /// root for every OTHER test class running in parallel (#3838).
    /// </param>
    /// <remarks>
    /// Named differently from <c>RunRunner</c>, and takes <c>string[]</c> rather than
    /// <c>params</c>, deliberately. An overload pair
    /// <c>(string, string, params string[])</c> / <c>(string, string, string?, params string[])</c>
    /// is not ambiguous to the compiler and picks the SECOND for a three-argument call, binding
    /// the caller's first flag to <c>noCacheRoot</c> and leaving <c>extraArgs</c> empty — so
    /// <c>RunRunner(bundle, pkg, "--cache DIR")</c> silently drops <c>--cache</c> and the child
    /// runs against the default cache root. Measured: that turned this file's sibling test red
    /// deterministically (A/B/A, three builds), and it fails as a MISSING cache entry, which
    /// reads like a caching defect rather than a dropped argument (#3849).
    /// </remarks>
    private static (string output, int exit) RunRunnerCore(
        string bundleDir, string absentPackageCache, string? noCacheRoot, string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundleDir}\"");
        args.Append($" --package-cache \"{absentPackageCache}\"");
        args.Append(" --verbose");
        foreach (var a in extraArgs) args.Append($" {a}");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        if (noCacheRoot != null)
            psi.Environment[AlRunner.Infrastructure.CacheRoots.NoCacheRootEnvVar] = noCacheRoot;
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void CacheThenNoCache_RedirectsAwayFromTheExplicitDir_NoCacheThenCache_UsesIt_BothLastWins()
    {
        TestArtifacts.SkipIfMissing();

        var scratchRoot = TestScratch.Dir("al-runner-nocache-lastwins");
        var bundleDir = Path.Combine(scratchRoot, "tests-app");
        var cacheDirA = Path.Combine(scratchRoot, "cache-a");
        var absentPackageCache = Path.Combine(scratchRoot, "no-such-package-cache");
        Directory.CreateDirectory(bundleDir);

        var testsId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(bundleDir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "Repro2555 Tests",
          "publisher": "Repro2555",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 61940, "to": 61949 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(bundleDir, "Repro2555Tests.al"), """
        codeunit 61940 "Repro2555 Poison Test"
        {
            Subtype = Test;

            [Test]
            procedure TrivialPass()
            begin
                if 1 + 1 <> 2 then
                    Error('arithmetic is broken');
            end;
        }
        """);

        // Run 1: plain `--cache cacheDirA` (no --no-cache at all) populates
        // cacheDirA/ncl-cecil with a real cache entry — establishes a baseline set of
        // files this test can prove is UNCHANGED by a later redirected run.
        var (output1, exit1) = RunRunner(bundleDir, absentPackageCache, $"--cache \"{cacheDirA}\"");
        Assert.True(exit1 == 0 && output1.Contains("1P/0F/0E"), $"run 1 must pass:\n{output1}");
        var nclCecilA = Path.Combine(cacheDirA, "ncl-cecil");
        Assert.True(Directory.Exists(nclCecilA) && Directory.GetFiles(nclCecilA, "*.dll").Length > 0,
            $"expected run 1 to populate {nclCecilA}:\n{output1}");
        var filesAfterRun1 = Directory.GetFiles(nclCecilA, "*.dll").Select(Path.GetFileName).OrderBy(x => x).ToArray();

        // Run 2: `--cache cacheDirA --no-cache` — --no-cache is LAST, so per #2555 it
        // must win for cacheDirA too: this run must NOT touch cacheDirA/ncl-cecil at
        // all (no new file) because ncl-cecil is redirected to a throwaway root
        // instead. Before the fix, cacheRootOverride stayed cacheDirA regardless of
        // the trailing --no-cache, so this run would resolve ncl-cecil (and every
        // other CacheRoots-backed cache) straight into cacheDirA — this run's log
        // would then mention cacheDirA's path (it legitimately says "[Cecil] Cecil
        // cache HIT" even on a REDIRECTED run, since one run reads its own
        // freshly-written ncl-cecil entry again across the shadow/Cecil re-exec — see
        // CacheRoots.NoCacheRootEnvVar's doc — so a HIT alone does not distinguish a
        // fix from a bug; which DIRECTORY it hit does).
        var (output2, exit2) = RunRunner(bundleDir, absentPackageCache, $"--cache \"{cacheDirA}\" --no-cache");
        Assert.True(exit2 == 0 && output2.Contains("1P/0F/0E"), $"run 2 must pass:\n{output2}");
        var filesAfterRun2 = Directory.GetFiles(nclCecilA, "*.dll").Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(filesAfterRun1, filesAfterRun2);
        Assert.DoesNotContain(cacheDirA, output2);

        // Run 3: `--no-cache --cache cacheDirA` — --cache is LAST, so it must win and
        // restore normal isolated-cache behaviour: cacheDirA already holds the exact
        // key from run 1 (same runner build, same BC artifact), so this is a HIT
        // against cacheDirA again — the mirror-image proof that the SAME flag pair in
        // the OPPOSITE order does NOT redirect away from cacheDirA.
        var (output3, exit3) = RunRunner(bundleDir, absentPackageCache, $"--no-cache --cache \"{cacheDirA}\"");
        Assert.True(exit3 == 0 && output3.Contains("1P/0F/0E"), $"run 3 must pass:\n{output3}");
        Assert.Contains("[Cecil] Cecil cache HIT", output3);
        var filesAfterRun3 = Directory.GetFiles(nclCecilA, "*.dll").Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(filesAfterRun1, filesAfterRun3);
    }

    [SkippableFact]
    public void NoCacheAlone_DoesNotLeakItsThrowawayTempDirectory()
    {
        TestArtifacts.SkipIfMissing();

        var scratchRoot = TestScratch.Dir("al-runner-nocache-noleak");
        var bundleDir = Path.Combine(scratchRoot, "tests-app");
        var absentPackageCache = Path.Combine(scratchRoot, "no-such-package-cache");
        Directory.CreateDirectory(bundleDir);

        var testsId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(bundleDir, "app.json"), $$"""
        {
          "id": "{{testsId}}",
          "name": "Repro2555 NoLeak Tests",
          "publisher": "Repro2555",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 61950, "to": 61959 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(bundleDir, "Repro2555NoLeakTests.al"), """
        codeunit 61950 "Repro2555 NoLeak Test"
        {
            Subtype = Test;

            [Test]
            procedure TrivialPass()
            begin
                if 1 + 1 <> 2 then
                    Error('arithmetic is broken');
            end;
        }
        """);

        // Name this run's throwaway root, rather than looking for whichever al-runner-no-cache-*
        // directories appear in the shared temp root while the run is in flight (#3849). Ten
        // other classes in this assembly spawn --no-cache runners — CacheGateProbeScopeTests,
        // CoverageTests, PlainRunInstrumentationGateTests, AlObjectEmitOrderDeterminismTests,
        // the four ServerAffectedSelection* files, both WatchPageMetadataReload* files — and
        // xunit.runner.json runs 4 collections at once, so a neighbour's live root is in the
        // temp root, is absent from `before`, and reads as this run's leak. Measured: with just
        // four of those classes selected the set difference named TWO directories at once, from
        // a test that spawns exactly ONE runner, and both were gone by the time the assertion
        // was read — they were neighbours mid-run, never leaks. CacheRootStartupFailureTests'
        // "al-runner-no-cache-root-expectations" matches that glob too.
        //
        // DisableForRun ADOPTS AL_RUNNER_NO_CACHE_ROOT when set, so this is the same code path
        // a re-exec'd child takes, and the run still gets exactly one throwaway root. The claim
        // is unchanged and the assertion is strictly stronger: not "no unexplained directory
        // appeared" but "the directory THIS run was told to use is gone, and so is its .owner
        // sidecar". A neighbour cannot satisfy or break it, because it names one path.
        var throwawayRoot = Path.Combine(
            Path.GetTempPath(), "al-runner-no-cache-scoped-" + Guid.NewGuid().ToString("N"));

        var (output, exit) = RunRunnerCore(
            bundleDir, absentPackageCache, throwawayRoot, new[] { "--no-cache" });
        Assert.True(exit == 0 && output.Contains("1P/0F/0E"), $"run must pass:\n{output}");

        // Positive: the run really USED the root it was handed — without this the negative below
        // would pass against a runner that ignored the variable and leaked a minted root instead.
        Assert.Contains(throwawayRoot, output, StringComparison.Ordinal);

        Assert.False(Directory.Exists(throwawayRoot),
            $"the --no-cache run left its throwaway root behind: {throwawayRoot}");
        Assert.False(File.Exists(AlRunner.Infrastructure.ScratchDirs.MarkerPathFor(throwawayRoot)),
            $"the --no-cache run left its throwaway root's .owner sidecar behind: {throwawayRoot}");
    }
}
