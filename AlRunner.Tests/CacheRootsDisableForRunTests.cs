// CacheRootsDisableForRunTests — unit-level coverage of
// AlRunner.Infrastructure.CacheRoots.DisableForRun/CleanupThrowawayRoot (issue #2555).
//
// #2555's bug: --no-cache only ever disabled the AL-output cache (Program.cs's
// alCacheDir); every OTHER cache resolved through CacheRoots.Resolve — compiled-deps,
// workspace-deps, ncl-cecil, bc-symbols, and (since #1821 shipped) ncl-shadow,
// app-manifests, r2r-chunks, install-baseline — stayed pointed at the real, shared,
// unscoped ~/.cache/al-runner/<name> regardless, so a run reached for specifically to
// reproduce or measure a cold compile still got most of what "cold" is supposed to cost.
//
// This file proves the MECHANISM directly: DisableForRun() must make Resolve() return
// paths under an actual, freshly-created directory (not just compute a different
// string), a file written into it must really land on disk, CleanupThrowawayRoot()
// must really delete it, and the AL_RUNNER_NO_CACHE_ROOT env var must make a SECOND
// call (simulating a re-exec'd child adopting the parent's choice) resolve to the
// SAME directory rather than minting a new one — the exact hazard the issue calls
// out ("handing the child a new throwaway root would make it miss, rewrite again,
// and take the exact load path the re-exec exists to avoid").
//
// The real Program.cs WIRING (does --no-cache actually call DisableForRun, does
// --cache/--no-cache last-wins hold both directions, is the wiring reachable across a
// real re-exec) is proved separately, end-to-end via a spawned subprocess, in
// NoCacheLastWinsIntegrationTests.cs — that is the "not just a computed path string"
// half this class does not attempt, mirroring how CacheRootsTests.cs (unit) and
// CacheRootsIsolationTests.cs (integration) split #1821's proof.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public sealed class CacheRootsDisableForRunTests
{
    private static void ClearEnvVar() =>
        Environment.SetEnvironmentVariable(CacheRoots.NoCacheRootEnvVar, null);

    [Fact]
    public void DisableForRun_MintsAFreshDirectoryUnderTempRoot_AndResolveUsesIt()
    {
        CacheRoots.ResetForTests();
        ClearEnvVar();
        try
        {
            var root = CacheRoots.DisableForRun();

            Assert.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), root);
            Assert.Equal(Path.Combine(root, "ncl-cecil"), CacheRoots.Resolve("ncl-cecil"));
            Assert.Equal(Path.Combine(root, "compiled-deps"), CacheRoots.Resolve("compiled-deps"));

            // Not just a string: real I/O against the resolved path must actually land
            // in this directory — the decisive claim of "genuinely redirected".
            var target = CacheRoots.Resolve("ncl-cecil");
            Directory.CreateDirectory(target);
            var probeFile = Path.Combine(target, "probe.txt");
            File.WriteAllText(probeFile, "hello");
            Assert.True(File.Exists(probeFile));
        }
        finally { CacheRoots.ResetForTests(); ClearEnvVar(); }
    }

    [Fact]
    public void DisableForRun_PublishesTheDirectory_ToTheEnvVar()
    {
        CacheRoots.ResetForTests();
        ClearEnvVar();
        try
        {
            var root = CacheRoots.DisableForRun();
            Assert.Equal(root, Environment.GetEnvironmentVariable(CacheRoots.NoCacheRootEnvVar));
        }
        finally { CacheRoots.ResetForTests(); ClearEnvVar(); }
    }

    [Fact]
    public void DisableForRun_WithEnvVarAlreadySet_AdoptsThatDirectory_NeverMintsASecondOne()
    {
        // Simulates a re-exec'd child: the parent already published a throwaway root
        // before handing off, and the child's own DisableForRun() call must land on the
        // EXACT SAME directory, not a fresh GUID — otherwise a key written by the
        // parent (e.g. ncl-cecil, written before the Cecil-rewrite re-exec) would be
        // invisible to the child and get redone, the exact cost the re-exec exists to
        // avoid paying twice.
        CacheRoots.ResetForTests();
        var preExisting = TestScratch.FlatDir("al-runner-no-cache-test-");
        Environment.SetEnvironmentVariable(CacheRoots.NoCacheRootEnvVar, preExisting);
        try
        {
            var root = CacheRoots.DisableForRun();
            Assert.Equal(preExisting, root);
            Assert.Equal(Path.Combine(preExisting, "bc-symbols"), CacheRoots.Resolve("bc-symbols"));
        }
        finally { CacheRoots.ResetForTests(); ClearEnvVar(); if (Directory.Exists(preExisting)) Directory.Delete(preExisting, true); }
    }

    [Fact]
    public void DisableForRun_CalledTwiceInTheSameProcess_ReturnsTheSameDirectoryBothTimes()
    {
        // Program.cs's own two re-exec decision points can each observe noCacheRequested
        // before either one actually hands off to a child — both calls, from the SAME
        // process, must agree on one directory.
        CacheRoots.ResetForTests();
        ClearEnvVar();
        try
        {
            var first = CacheRoots.DisableForRun();
            var second = CacheRoots.DisableForRun();
            Assert.Equal(first, second);
        }
        finally { CacheRoots.ResetForTests(); ClearEnvVar(); }
    }

    [Fact]
    public void CleanupThrowawayRoot_DeletesTheMintedDirectory_IncludingWhateverWasWrittenIntoIt()
    {
        CacheRoots.ResetForTests();
        ClearEnvVar();
        try
        {
            var root = CacheRoots.DisableForRun();
            var target = CacheRoots.Resolve("workspace-deps");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "app.bin"), "not a real app");
            Assert.True(Directory.Exists(root));
            // #2706: the minted root is owned through ScratchDirs, so a --no-cache run that is
            // killed before this cleanup runs is reclaimed by the next runner start.
            Assert.True(File.Exists(AlRunner.Infrastructure.ScratchDirs.MarkerPathFor(root)),
                "the --no-cache throwaway root has no .owner sidecar");

            CacheRoots.CleanupThrowawayRoot();

            Assert.False(Directory.Exists(root), $"expected {root} to be gone after cleanup");
            Assert.False(File.Exists(AlRunner.Infrastructure.ScratchDirs.MarkerPathFor(root)),
                "cleanup left the .owner sidecar behind");
        }
        finally { CacheRoots.ResetForTests(); ClearEnvVar(); }
    }

    [Fact]
    public void CleanupThrowawayRoot_WithoutDisableForRunHavingBeenCalled_IsANoOp_DoesNotThrow()
    {
        CacheRoots.ResetForTests();
        ClearEnvVar();
        try
        {
            CacheRoots.CleanupThrowawayRoot(); // must not throw
        }
        finally { CacheRoots.ResetForTests(); ClearEnvVar(); }
    }

    [Fact]
    public void CleanupThrowawayRoot_WhenDirectoryWasNeverActuallyCreated_IsANoOp_DoesNotThrow()
    {
        // DisableForRun() mints a NAME but nothing necessarily calls
        // Directory.CreateDirectory against it before cleanup runs (e.g. a run that
        // exits before touching any of these caches). Cleanup must tolerate that.
        CacheRoots.ResetForTests();
        ClearEnvVar();
        try
        {
            var root = CacheRoots.DisableForRun();
            Assert.False(Directory.Exists(root));

            CacheRoots.CleanupThrowawayRoot(); // must not throw even though nothing was ever created
        }
        finally { CacheRoots.ResetForTests(); ClearEnvVar(); }
    }

    [Fact]
    public void SetOverride_AfterDisableForRun_ReplacesTheThrowawayRedirect()
    {
        // Mirrors Program.cs's own last-wins handling: a --cache flag appearing AFTER a
        // --no-cache flag on the same command line must fully replace the throwaway
        // redirect, for every cache resolved here — not layer underneath it.
        CacheRoots.ResetForTests();
        ClearEnvVar();
        try
        {
            var throwaway = CacheRoots.DisableForRun();
            var explicitDir = TestScratch.Dir("al-runner-cacheroots-explicit");

            CacheRoots.SetOverride(explicitDir);

            Assert.Equal(Path.Combine(explicitDir, "ncl-cecil"), CacheRoots.Resolve("ncl-cecil"));
            Assert.NotEqual(Path.Combine(throwaway, "ncl-cecil"), CacheRoots.Resolve("ncl-cecil"));
        }
        finally { CacheRoots.ResetForTests(); ClearEnvVar(); }
    }
}
