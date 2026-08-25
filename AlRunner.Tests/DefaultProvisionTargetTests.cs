// DefaultProvisionTargetTests — proves BcArtifacts.ResolveProvisionTargetCore, the fix for
// issue #2033.
//
// Root cause: on the auto-provision default path (no --bc-version / --artifact-path),
// Program.cs used to call BcArtifacts.DefaultVersionPrefix — which answers "what does the
// LOCAL CACHE already have" — and handed that pre-collapsed value to provisioning. On a
// genuinely empty cache DefaultVersionPrefix has nothing to look at for either the engine's
// exact build or its minor, so it falls straight through to "major only" (e.g. "28") BEFORE
// a single byte has been downloaded. Provisioning then resolved "latest full version for
// major 28" from the CDN — landing on 28.4 while the shipped engine was built for 28.1,
// putting a fresh install straight into the KNOWN-DEGRADED engine/artifact skew #2020
// describes, silently, on the very first run.
//
// The fix: when auto-provisioning is going to run anyway, ask the SAME three tiers (exact
// build, then minor, then major) whether they're available from EITHER the cache or the
// CDN before giving up and falling looser — so provisioning fetches the version selection
// actually wants. These tests exercise the pure core with fake cache/CDN state — no
// network, no BC engine — proving each tier decision and, most importantly, that a cache
// miss with a CDN hit does NOT collapse to the major (the exact defect from #2033).
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class DefaultProvisionTargetTests : IDisposable
{
    private readonly string _root;

    public DefaultProvisionTargetTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-provision-target", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void MakeVersionDirs(params string[] versions)
    {
        foreach (var v in versions) Directory.CreateDirectory(Path.Combine(_root, v));
    }

    /// <summary>
    /// THE #2033 proving case: a genuinely empty cache (nothing cached at all — the real
    /// first-run shape) with the engine's own exact build actually published on the CDN
    /// must select THAT build, not collapse to "major only" the way DefaultVersionPrefix
    /// alone would. This is the exact scenario that landed a first run on BC 28.4 while
    /// the engine was built for 28.1.
    /// </summary>
    [Fact]
    public void EmptyCache_CdnHasEngineExactBuild_SelectsExactBuild_NotMajorFallback()
    {
        var engineVersion = new Version("28.1.49838.50794");

        var result = BcArtifacts.ResolveProvisionTargetCore(
            engineVersion, _root,
            cdnHasExactVersion: v => v == "28.1.49838.50794",
            cdnResolvePrefix: p => throw new InvalidOperationException(
                $"must not fall through to prefix resolution when the exact build is on the CDN (asked for '{p}')"),
            out var tier);

        Assert.Equal("28.1.49838.50794", result);
        Assert.Equal("cdn-exact", tier);

        // Contrast with the cache-only function this replaces on the auto-provision path:
        // it has nothing cached to look at, so it degrades to "28" — the defect #2033 fixes.
        Assert.Equal("28", BcArtifacts.DefaultVersionPrefix(engineVersion, _root));
    }

    /// <summary>
    /// The exact build was withdrawn / never published (issue #2010's scenario) but the
    /// engine's own MINOR still has a build on the CDN — must fall back one tier, to that
    /// resolved build, not all the way to the major.
    /// </summary>
    [Fact]
    public void EmptyCache_CdnLacksExactBuild_ButHasEngineMinor_ResolvesToLatestOfThatMinor()
    {
        var engineVersion = new Version("28.1.49838.50794");

        var result = BcArtifacts.ResolveProvisionTargetCore(
            engineVersion, _root,
            cdnHasExactVersion: v => false, // withdrawn build, e.g. #2010
            cdnResolvePrefix: p => p == "28.1" ? "28.1.55555.66666" : null,
            out var tier);

        Assert.Equal("28.1.55555.66666", result);
        Assert.Equal("cdn-minor", tier);
    }

    /// <summary>
    /// Genuinely degraded: neither the exact build nor the engine's own minor exists
    /// anywhere (cache or CDN). Only NOW may this fall back to the bare major — and the
    /// caller (Program.cs) must print the KNOWN-DEGRADED warning for this tier specifically.
    /// </summary>
    [Fact]
    public void EmptyCache_CdnHasNeitherExactNorMinor_FallsBackToMajor()
    {
        var engineVersion = new Version("28.1.49838.50794");

        var result = BcArtifacts.ResolveProvisionTargetCore(
            engineVersion, _root,
            cdnHasExactVersion: v => false,
            cdnResolvePrefix: p => null,
            out var tier);

        Assert.Equal("28", result);
        Assert.Equal("major-fallback", tier);
    }

    /// <summary>
    /// A cached exact build wins outright — no CDN probe needed or performed. Proves the
    /// fast path stays fast and offline-safe when nothing needs fetching.
    /// </summary>
    [Fact]
    public void CachedExactBuild_WinsWithoutConsultingTheCdnAtAll()
    {
        var engineVersion = new Version("28.1.49838.50794");
        MakeVersionDirs("28.1.49838.50794", "28.2.50931.52786");

        var result = BcArtifacts.ResolveProvisionTargetCore(
            engineVersion, _root,
            cdnHasExactVersion: v => throw new InvalidOperationException("must not probe the CDN when already cached"),
            cdnResolvePrefix: p => throw new InvalidOperationException("must not probe the CDN when already cached"),
            out var tier);

        Assert.Equal("28.1.49838.50794", result);
        Assert.Equal("cached-exact", tier);
    }

    /// <summary>
    /// The engine's minor is cached (but not its exact build) — must win over a CDN probe
    /// for the exact build being unavailable, without ever falling to major.
    /// </summary>
    [Fact]
    public void CachedMinor_WinsOverMajorFallback_WhenExactBuildNotCachedOrOnCdn()
    {
        var engineVersion = new Version("28.1.49838.50794");
        MakeVersionDirs("28.1.60000.60000"); // same minor, different (higher) build

        var result = BcArtifacts.ResolveProvisionTargetCore(
            engineVersion, _root,
            cdnHasExactVersion: v => false,
            cdnResolvePrefix: p => throw new InvalidOperationException(
                "must not resolve via CDN when the engine's own minor is already cached"),
            out var tier);

        Assert.Equal("28.1", result);
        Assert.Equal("cached-minor", tier);
    }
}
