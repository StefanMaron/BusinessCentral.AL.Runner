// RelativeCacheRootTests — issue #3084.
//
// THE DEFECT
//   `--cache` accepts a relative directory and CacheRoots stored it verbatim, so every path
//   CacheRoots.Resolve derived from it stayed relative. One of those paths — the r2r-chunks
//   cache — is handed straight to AssemblyLoadContext.LoadFromAssemblyPath, which REQUIRES an
//   absolute path and refuses a relative one outright. Every extracted R2R chunk of Base
//   Application / System Application / Business Foundation therefore failed to load, the run
//   dropped to a tier where those apps' objects do not exist, and it reported ordinary test
//   failures. Reproduced on tests/runner-extras/microsoft-test-library, BC 28.1, identical
//   build and package caches, the ONLY difference being the form of the --cache value:
//
//       --cache .measure/relcache    -> exit 1, 3 tests, 0 pass / 3 fail, 16 [provision-gap]
//       --cache "$PWD/.measure/abs"  -> exit 0, 3 tests, 3 pass / 0 fail,  0 [provision-gap]
//
//   Silent in the way that matters: nothing says "your cache path is relative". It says
//   "'Microsoft Base Application' has a precompiled sidecar DLL that could not be loaded ...
//   Fix: rebuild or replace the sidecar DLL" — advice that is wrong for this cause — and then
//   the run continues and fails tests, which a reader takes for an unprovisioned dependency.
//
// WHAT EACH TEST HERE CLAIMS
//   1-3  Resolve roots and normalizes what SetOverride was given, and leaves an already-
//        absolute value exactly as it was. The cheap "is the path computed correctly" half.
//   4    The decisive one: drive the real producer (AppLoader.ExtractAllDllPaths) under a
//        relative override and assert the paths it hands back are accepted by the actual API
//        the loader calls with them — LoadFromAssemblyPath — instead of asserting a property
//        that merely correlates with that. Pre-fix this throws ArgumentException("... is not
//        an absolute path"); post-fix the only thing left to complain about is the bytes.
//   5    The loud half. A cache root that is somehow still not absolute must abort with a
//        message naming the value, the flag and the CONSEQUENCE, not degrade into a wrong
//        answer. Reached through an internal seam because both production writers of the
//        override now root what they store — see SetOverrideBypassingRootingForTests.
//   6    The sibling-writer invariant: DisableForRun adopts a raw environment value, and it
//        must root that too, or the two writers of one field disagree about the invariant.
//
//   The link from DependencyLoader to the producer this file drives is already pinned by
//   CorruptSidecarLoaderCallSiteTests, whose Tier-2 case asserts the reported chunk path
//   lives under `r2r-chunks` — so loader -> ExtractAllDllPaths -> rooted path is covered end
//   to end across the two files. That test is in RecordPatchesSerialCollection (it reads the
//   process-global ProvisionGapLog) and this class must be in CacheRootsSerialCollection (it
//   writes the process-global cache override), which is why the two claims live apart.
//
// Nothing here is a claim about Business Central: path resolution and assembly loading are
// runner infrastructure, invisible to AL, and the al-language corpus has no way to pass a
// --cache value at all. Nothing in this file belongs upstream.

using System.IO.Compression;
using System.Runtime.Loader;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public sealed class RelativeCacheRootTests : IDisposable
{
    // An owned scratch directory, named RELATIVELY. Path.GetRelativePath against the test
    // host's working directory gives a genuinely non-rooted string that still points at a
    // directory ScratchDirs will reclaim — better than a bare "relcache" leaf, which would
    // litter the build output and, worse, resolve differently depending on where the test
    // host was launched from.
    private readonly string _absScratch = TestScratch.Dir("al-runner-relative-cache-root");
    private readonly string _relScratch;

    public RelativeCacheRootTests()
        => _relScratch = Path.GetRelativePath(Environment.CurrentDirectory, _absScratch);

    public void Dispose()
    {
        CacheRoots.ResetForTests();
        try { Directory.Delete(_absScratch, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Resolve_RelativeOverride_IsRootedAgainstTheWorkingDirectory()
    {
        Assert.False(Path.IsPathRooted(_relScratch), "the fixture must actually be relative");

        CacheRoots.SetOverride(_relScratch);
        try
        {
            // The exact value, not just "it is rooted": a fix that rooted against the wrong
            // base would satisfy IsPathRooted and still write to the wrong disk location.
            Assert.Equal(Path.Combine(_absScratch, "r2r-chunks"), CacheRoots.Resolve("r2r-chunks"));
            Assert.Equal(Path.Combine(_absScratch, "compiled-deps"), CacheRoots.Resolve("compiled-deps"));
            Assert.Equal(Path.Combine(_absScratch, "ncl-shadow"), CacheRoots.Resolve("ncl-shadow"));

            // And the whole point, stated as the precondition the consumer imposes.
            Assert.True(Path.IsPathRooted(CacheRoots.Resolve("r2r-chunks")));
        }
        finally { CacheRoots.ResetForTests(); }
    }

    [Fact]
    public void Resolve_RelativeOverrideWithDotSegments_IsNormalizedNotJustConcatenated()
    {
        // Path.Combine would have produced "<parent>/nonexistent-sibling/../<leaf>/r2r-chunks",
        // a string that resolves correctly for File.Exists and is still not what any tool
        // comparing two cache paths would call equal. GetFullPath collapses it.
        var parent = Path.GetDirectoryName(_absScratch)!;
        var leaf = Path.GetFileName(_absScratch);
        var relParent = Path.GetRelativePath(Environment.CurrentDirectory, parent);
        var noisy = Path.Combine(relParent, "nonexistent-sibling", "..", leaf);

        Assert.False(Path.IsPathRooted(noisy));

        CacheRoots.SetOverride(noisy);
        try
        {
            Assert.Equal(Path.Combine(_absScratch, "r2r-chunks"), CacheRoots.Resolve("r2r-chunks"));
            Assert.DoesNotContain("..", CacheRoots.Resolve("r2r-chunks"), StringComparison.Ordinal);
        }
        finally { CacheRoots.ResetForTests(); }
    }

    [Fact]
    public void Resolve_AbsoluteOverride_IsUnchangedByTheRooting()
    {
        // The control. Rooting must be a no-op for the value every existing caller already
        // passes, or this change would silently relocate every warm cache in existence.
        CacheRoots.SetOverride(_absScratch);
        try
        {
            Assert.Equal(Path.Combine(_absScratch, "r2r-chunks"), CacheRoots.Resolve("r2r-chunks"));
            Assert.Equal(Path.Combine(_absScratch, "bc-symbols"), CacheRoots.Resolve("bc-symbols"));
        }
        finally { CacheRoots.ResetForTests(); }
    }

    /// <summary>
    /// The decisive test: the paths the r2r-chunks producer hands back must be paths the API
    /// the loader calls with them will actually accept.
    ///
    /// The chunk bytes are deliberately a 5-byte non-PE, the same shape
    /// CorruptSidecarLoaderCallSiteTests uses — extraction never parses them, and using
    /// garbage keeps this test from having to find a real assembly it can load into the
    /// default context without colliding with an identity already loaded there. That makes
    /// the assertion an exclusion rather than a success: LoadFromAssemblyPath must reject
    /// these bytes for what they CONTAIN (BadImageFormatException), never for where they
    /// LIVE (ArgumentException, "is not an absolute path"), which is the only complaint it
    /// had pre-fix and the one that produced the silent tier drop.
    /// </summary>
    [Fact]
    public void ExtractAllDllPaths_UnderARelativeCacheOverride_ProducesPathsLoadFromAssemblyPathAccepts()
    {
        var appPath = WriteSyntheticR2RApp();

        CacheRoots.SetOverride(_relScratch);
        try
        {
            var chunks = AppLoader.ExtractAllDllPaths(appPath);
            Assert.Equal(2, chunks.Count);

            foreach (var chunk in chunks)
            {
                Assert.True(Path.IsPathRooted(chunk), $"chunk path must be absolute: {chunk}");
                Assert.True(File.Exists(chunk), $"chunk must exist on disk: {chunk}");
                Assert.StartsWith(Path.Combine(_absScratch, "r2r-chunks"), chunk, StringComparison.Ordinal);

                var ex = Assert.ThrowsAny<Exception>(
                    () => AssemblyLoadContext.Default.LoadFromAssemblyPath(chunk));
                Assert.IsType<BadImageFormatException>(ex);
                Assert.DoesNotContain("is not an absolute path", ex.Message, StringComparison.Ordinal);
            }
        }
        finally { CacheRoots.ResetForTests(); }
    }

    [Fact]
    public void Resolve_WithAnUnrootedOverride_ThrowsNamingTheValueTheFlagAndTheConsequence()
    {
        CacheRoots.SetOverrideBypassingRootingForTests("some-relative-cache");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => CacheRoots.Resolve("r2r-chunks"));

            // Every part a reader needs in order NOT to go looking at their package cache.
            Assert.Contains("some-relative-cache", ex.Message, StringComparison.Ordinal);
            Assert.Contains("r2r-chunks", ex.Message, StringComparison.Ordinal);
            Assert.Contains("--cache", ex.Message, StringComparison.Ordinal);
            Assert.Contains("LoadFromAssemblyPath", ex.Message, StringComparison.Ordinal);
            Assert.Contains("#3084", ex.Message, StringComparison.Ordinal);
        }
        finally { CacheRoots.ResetForTests(); }
    }

    [Fact]
    public void Resolve_WithARootedOverride_DoesNotThrow()
    {
        // The negative half of the guard: it must fire on the shape it is for, and on nothing
        // else. Without this, a guard that threw unconditionally would pass the test above.
        CacheRoots.SetOverrideBypassingRootingForTests(_absScratch);
        try
        {
            Assert.Equal(Path.Combine(_absScratch, "r2r-chunks"), CacheRoots.Resolve("r2r-chunks"));
        }
        finally { CacheRoots.ResetForTests(); }
    }

    [Fact]
    public void DisableForRun_AdoptingARelativeEnvironmentRoot_RootsItAndRepublishesTheRootedForm()
    {
        var previous = Environment.GetEnvironmentVariable(CacheRoots.NoCacheRootEnvVar);
        Environment.SetEnvironmentVariable(CacheRoots.NoCacheRootEnvVar, _relScratch);
        try
        {
            var root = CacheRoots.DisableForRun();

            Assert.Equal(_absScratch, root);
            Assert.Equal(Path.Combine(_absScratch, "r2r-chunks"), CacheRoots.Resolve("r2r-chunks"));
            // Republished, so a re-exec'd child inherits the absolute form instead of
            // re-resolving the same relative string against its own working directory.
            Assert.Equal(_absScratch, Environment.GetEnvironmentVariable(CacheRoots.NoCacheRootEnvVar));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CacheRoots.NoCacheRootEnvVar, previous);
            CacheRoots.ResetForTests();
        }
    }

    /// <summary>
    /// A syntactically valid .app carrying two `publishedartifacts/*.dll` entries — enough for
    /// AppLoader.IsR2R and ExtractAllDlls, and two chunks rather than one so a fix that
    /// happened to root only the first would still fail.
    /// </summary>
    private string WriteSyntheticR2RApp()
    {
        Directory.CreateDirectory(_absScratch);
        var appPath = Path.Combine(_absScratch, "RelCacheFixture_R2RDep_1.0.0.0.app");
        byte[] bogusPe = { 0x4D, 0x5A, 0x90, 0x00, 0x03 };
        using (var zip = ZipFile.Open(appPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "publishedartifacts/chunk000.dll", "publishedartifacts/chunk001.dll" })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bogusPe, 0, bogusPe.Length);
            }
        }
        return appPath;
    }
}
