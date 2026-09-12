// Issue #2226: provision's platform-app / test-toolkit sub-step must target the selected
// engine's own 4-part build, not the CDN's latest build of that major.minor. Fake CDN
// delegates only; no network.
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Provisioning;
using Xunit;

namespace AlRunner.Tests;

public sealed class ManifestAppsBuildTargetTests : IDisposable
{
    private const string Engine = "28.1.49838.53910";
    private const string CdnLatest = "28.1.49838.54044";

    private readonly string _root;
    private readonly List<string> _log = new();
    private readonly List<string> _prefixQueries = new();

    public ManifestAppsBuildTargetTests()
    {
        _root = TestScratch.Dir("al-runner-manifest-apps-build");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private CdnPrefixResult FakeIndex(string prefix)
    {
        _prefixQueries.Add(prefix);
        return prefix == "28.1" ? CdnPrefixResult.Resolved(CdnLatest) : CdnPrefixResult.NoMatch;
    }

    [Fact]
    public void PublishedEngineBuild_IsTargeted_NotTheCdnLatestOfItsMinor()
    {
        var result = ProvisioningCheck.ResolveManifestAppsBuildCore(Engine,
            v => v == Engine ? CdnProbeResult.Published : CdnProbeResult.NotPublished,
            FakeIndex, _log.Add);

        Assert.Equal(Engine, result);
        Assert.Empty(_prefixQueries);
        Assert.Empty(_log);
    }

    [Fact]
    public void UnansweredProbe_HoldsTheEngineBuild()
    {
        var result = ProvisioningCheck.ResolveManifestAppsBuildCore(Engine,
            _ => CdnProbeResult.Undetermined(NetworkFailureKind.Timeout),
            FakeIndex, _log.Add);

        Assert.Equal(Engine, result);
        Assert.Empty(_prefixQueries);
    }

    [Fact]
    public void WithdrawnEngineBuild_FallsBackToLatestOfMinor_AndSaysSo()
    {
        var result = ProvisioningCheck.ResolveManifestAppsBuildCore(Engine,
            _ => CdnProbeResult.NotPublished,
            FakeIndex, _log.Add);

        Assert.Equal(CdnLatest, result);
        Assert.Equal(new[] { "28.1" }, _prefixQueries);
        var note = Assert.Single(_log);
        Assert.Contains($"BC {Engine} is not published", note);
        Assert.Contains($"fetching BC {CdnLatest} instead", note);
    }

    [Fact]
    public void WithdrawnEngineBuild_NothingInIndex_ReturnsNull()
    {
        var result = ProvisioningCheck.ResolveManifestAppsBuildCore("27.9.1.2",
            _ => CdnProbeResult.NotPublished,
            FakeIndex, _log.Add);

        Assert.Null(result);
        Assert.Empty(_log);
    }

    [Fact]
    public void MajorMinorPrefix_ResolvesLatestOfThatMinor_WithoutProbing()
    {
        var probed = false;
        var result = ProvisioningCheck.ResolveManifestAppsBuildCore("28.1",
            _ => { probed = true; return CdnProbeResult.Published; },
            FakeIndex, _log.Add);

        Assert.Equal(CdnLatest, result);
        Assert.False(probed);
    }

    [Fact]
    public void WarmReuse_PrefersTheEngineBuild_OverANewerCompleteBuildOfTheSameMinor()
    {
        Directory.CreateDirectory(Path.Combine(_root, Engine));
        Directory.CreateDirectory(Path.Combine(_root, CdnLatest));

        // No required apps and no toolkit: every candidate is vacuously complete, so the
        // answer is purely the candidate order.
        var preferred = ProgramSupport.FindWarmProvisionedVersion(
            _root, "28.1", Array.Empty<string>(), needTest: false, preferredVersion: Engine);
        var unpreferred = ProgramSupport.FindWarmProvisionedVersion(
            _root, "28.1", Array.Empty<string>(), needTest: false);

        Assert.Equal(Engine, preferred);
        Assert.Equal(CdnLatest, unpreferred);
    }
}
