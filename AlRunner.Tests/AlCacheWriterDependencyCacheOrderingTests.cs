// AlCacheWriterDependencyCacheOrderingTests — pins the write-ordering/atomicity contract
// DependencyLoader.PublishSourceDependencyCache uses for its `compiled-deps`
// source-dependency cache (issue #1809 follow-up, flagged during PR review:
// https://github.com/StefanMaron/BusinessCentral.AL.Runner/pull/1818#issuecomment-5266348106).
//
// DependencyLoader.LoadOne's cachedDll read gate is a plain `File.Exists(cachedDll)` — it
// does not check the five sidecars (report-metadata / report-layouts / page-metadata /
// xmlport-metadata / enum-registry) independently. That means "the DLL is visible" is
// treated as "the whole cache entry, sidecars included, is ready to replay" — the same
// completeness contract AlCacheSidecars.IsCompleteEntry already relies on for the
// bundle-level AL-output cache (issue #1810/#1812).
//
// Before this fix, PublishSourceDependencyCache's logic (inlined in LoadOne at the time)
// wrote the DLL FIRST via a plain, non-atomic File.WriteAllBytes, then the five sidecars
// after it, also non-atomically. That has two independent hazards this test targets
// directly by calling the real production method (extracted out of LoadOne specifically
// so it's reachable without a full BC compile):
//   1. Ordering: a concurrent reader could see cachedDll fully written (File.Exists ⇒
//      true) while one or more sidecars were still missing — a partial-HIT replay.
//   2. Atomicity: the DLL write itself was a torn-read hazard if a second process raced
//      the same content-derived cache key (BadImageFormatException on Assembly.Load).
//
// Both are now fixed by routing every artifact through AlCacheWriter.AtomicPublish and
// publishing the DLL LAST.
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlCacheWriterDependencyCacheOrderingTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-cache-writer-dep-order-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void PublishSourceDependencyCache_DllNeverVisibleBeforeAnySidecar()
    {
        var dir = NewTempDir();
        try
        {
            var key = "abc123";
            var reportSidecar = Path.Combine(dir, key + ".report-metadata.json");
            var reportLayoutSidecar = Path.Combine(dir, key + ".report-layouts.json");
            var pageMetadataSidecar = Path.Combine(dir, key + ".page-metadata.json");
            var xmlPortMetadataSidecar = Path.Combine(dir, key + ".xmlport-metadata.json");
            var enumRegistrySidecar = Path.Combine(dir, key + ".enum-registry.json");
            var cachedDll = Path.Combine(dir, key + ".dll");
            var sidecarPaths = new[]
            {
                reportSidecar, reportLayoutSidecar, pageMetadataSidecar,
                xmlPortMetadataSidecar, enumRegistrySidecar,
            };

            // Empty id sets everywhere — this test's claim is about ordering/atomicity of
            // the on-disk artifacts, not about the metadata content (that's covered by
            // SourceDepCacheEnumMetadataTests via a full runner invocation). Empty ids
            // still produce valid, complete sidecar JSON (each SaveSidecar filters its
            // registry by id set and serializes whatever's left, including empty).
            var (sidecarCount, enumSidecarCount) = DependencyLoader.PublishSourceDependencyCache(
                cachedDll, new byte[] { 1, 2, 3, 4 },
                reportSidecar, Array.Empty<int>(),
                reportLayoutSidecar,
                pageMetadataSidecar, Array.Empty<int>(),
                xmlPortMetadataSidecar, Array.Empty<int>(),
                enumRegistrySidecar, Array.Empty<int>());

            Assert.Equal(0, sidecarCount);
            Assert.Equal(0, enumSidecarCount);

            // The end state: everything published, DLL content intact and unmixed with
            // any placeholder/temp bytes.
            foreach (var sidecar in sidecarPaths)
                Assert.True(File.Exists(sidecar), $"expected {Path.GetFileName(sidecar)} to be published");
            Assert.True(File.Exists(cachedDll));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(cachedDll));

            // No leftover .tmp artifacts from any of the six AtomicPublish calls.
            var leftovers = Directory.GetFiles(dir)
                .Where(f => !sidecarPaths.Contains(f) && !string.Equals(f, cachedDll))
                .ToArray();
            Assert.Empty(leftovers);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Deterministic proof of the DLL-last ORDERING invariant (not just the end state):
    // uses PublishSourceDependencyCache's onSidecarsPublishedBeforeDll test seam to
    // observe filesystem state at the EXACT point a concurrent reader gated on
    // `File.Exists(cachedDll)` (LoadOne) could land — after all five sidecars are
    // committed, before the DLL is. No polling/timing race required: the seam runs
    // synchronously on the same call stack, between the last sidecar's AtomicPublish and
    // the DLL's.
    [Fact]
    public void PublishSourceDependencyCache_AllSidecarsCommitted_BeforeDllBecomesVisible()
    {
        var dir = NewTempDir();
        try
        {
            var key = "def456";
            var reportSidecar = Path.Combine(dir, key + ".report-metadata.json");
            var reportLayoutSidecar = Path.Combine(dir, key + ".report-layouts.json");
            var pageMetadataSidecar = Path.Combine(dir, key + ".page-metadata.json");
            var xmlPortMetadataSidecar = Path.Combine(dir, key + ".xmlport-metadata.json");
            var enumRegistrySidecar = Path.Combine(dir, key + ".enum-registry.json");
            var cachedDll = Path.Combine(dir, key + ".dll");

            var hookRan = false;
            bool dllExistedAtHookTime = true; // start "true" so a no-op hook can't fake a pass
            bool[] sidecarsExistedAtHookTime = new bool[5];

            DependencyLoader.PublishSourceDependencyCache(
                cachedDll, new byte[] { 9 },
                reportSidecar, Array.Empty<int>(),
                reportLayoutSidecar,
                pageMetadataSidecar, Array.Empty<int>(),
                xmlPortMetadataSidecar, Array.Empty<int>(),
                enumRegistrySidecar, Array.Empty<int>(),
                onSidecarsPublishedBeforeDll: () =>
                {
                    hookRan = true;
                    dllExistedAtHookTime = File.Exists(cachedDll);
                    sidecarsExistedAtHookTime[0] = File.Exists(reportSidecar);
                    sidecarsExistedAtHookTime[1] = File.Exists(reportLayoutSidecar);
                    sidecarsExistedAtHookTime[2] = File.Exists(pageMetadataSidecar);
                    sidecarsExistedAtHookTime[3] = File.Exists(xmlPortMetadataSidecar);
                    sidecarsExistedAtHookTime[4] = File.Exists(enumRegistrySidecar);
                });

            Assert.True(hookRan);
            Assert.False(dllExistedAtHookTime,
                "the DLL must not be visible until every sidecar it depends on is already committed");
            Assert.All(sidecarsExistedAtHookTime, existed => Assert.True(existed));

            // And after the call returns, everything is in its final, complete state.
            Assert.True(File.Exists(cachedDll));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
