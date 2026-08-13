// CacheRootsTests — unit-level coverage of AlRunner.Infrastructure.CacheRoots.Resolve
// (issue #1821). This is the cheap "is the path computed correctly" half; the
// decisive "does it actually WRITE there and not to the shared real location" half is
// CacheRootsIsolationTests.cs's end-to-end subprocess proof — see that file's header
// for why a path-only test alone would not be sufficient evidence for this bug.
//
// All test methods stay in ONE class so they run sequentially (xunit's default within
// a class/collection) despite this project's parallelizeTestCollections=true. CacheRoots
// is process-global mutable state, and several BcAppSymbolCache test classes touch it
// too — indirectly, via BcAppSymbolCache.Get()/GetTableExtensions() in-process calling
// CacheRoots.Resolve("bc-symbols") — so this class joins CacheRootsSerialCollection
// alongside them (see that file's header) rather than relying on being the only one.
// Each test still resets the override in a try/finally, belt-and-braces.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public sealed class CacheRootsTests
{
    [Fact]
    public void Resolve_NoOverride_DefaultsToRealUserProfileCacheDir()
    {
        CacheRoots.ResetForTests();
        try
        {
            var expectedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "al-runner");

            Assert.Equal(Path.Combine(expectedRoot, "compiled-deps"), CacheRoots.Resolve("compiled-deps"));
            Assert.Equal(Path.Combine(expectedRoot, "workspace-deps"), CacheRoots.Resolve("workspace-deps"));
            Assert.Equal(Path.Combine(expectedRoot, "ncl-cecil"), CacheRoots.Resolve("ncl-cecil"));
            Assert.Equal(Path.Combine(expectedRoot, "bc-symbols"), CacheRoots.Resolve("bc-symbols"));
        }
        finally { CacheRoots.ResetForTests(); }
    }

    [Fact]
    public void Resolve_WithOverride_NestsEachCacheUnderTheGivenDir()
    {
        var overrideDir = Path.Combine(Path.GetTempPath(), "al-runner-cacheroots-unit", Guid.NewGuid().ToString("N"));
        CacheRoots.SetOverride(overrideDir);
        try
        {
            Assert.Equal(Path.Combine(overrideDir, "compiled-deps"), CacheRoots.Resolve("compiled-deps"));
            Assert.Equal(Path.Combine(overrideDir, "workspace-deps"), CacheRoots.Resolve("workspace-deps"));
            Assert.Equal(Path.Combine(overrideDir, "ncl-cecil"), CacheRoots.Resolve("ncl-cecil"));
            Assert.Equal(Path.Combine(overrideDir, "bc-symbols"), CacheRoots.Resolve("bc-symbols"));
        }
        finally { CacheRoots.ResetForTests(); }
    }

    [Fact]
    public void Resolve_TwoDifferentOverrides_NeverCollide()
    {
        var dirA = Path.Combine(Path.GetTempPath(), "al-runner-cacheroots-unit", Guid.NewGuid().ToString("N"));
        var dirB = Path.Combine(Path.GetTempPath(), "al-runner-cacheroots-unit", Guid.NewGuid().ToString("N"));
        try
        {
            CacheRoots.SetOverride(dirA);
            var resolvedA = CacheRoots.Resolve("compiled-deps");

            CacheRoots.SetOverride(dirB);
            var resolvedB = CacheRoots.Resolve("compiled-deps");

            Assert.NotEqual(resolvedA, resolvedB);
            Assert.StartsWith(dirA, resolvedA);
            Assert.StartsWith(dirB, resolvedB);
        }
        finally { CacheRoots.ResetForTests(); }
    }

    [Fact]
    public void Resolve_SetOverrideNull_RevertsToDefault()
    {
        var overrideDir = Path.Combine(Path.GetTempPath(), "al-runner-cacheroots-unit", Guid.NewGuid().ToString("N"));
        try
        {
            CacheRoots.SetOverride(overrideDir);
            Assert.StartsWith(overrideDir, CacheRoots.Resolve("bc-symbols"));

            // Mirrors Program.cs: --no-cache (or simply no --cache flag at all) never
            // sets cacheRootOverride, so a null SetOverride call must behave exactly
            // like ResetForTests — reverting to the real ~/.cache/al-runner default.
            CacheRoots.SetOverride(null);
            var expectedDefault = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "al-runner", "bc-symbols");
            Assert.Equal(expectedDefault, CacheRoots.Resolve("bc-symbols"));
        }
        finally { CacheRoots.ResetForTests(); }
    }
}
