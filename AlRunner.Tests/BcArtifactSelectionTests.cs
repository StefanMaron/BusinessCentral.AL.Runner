// BcArtifactSelectionTests — version selection is engine-major-constrained.
//
// Step 2 of the provisioning work: when the user pins neither --bc-version nor
// --artifact-path, the runner defaults the artifact selection to the ENGINE's built
// MAJOR (the only major this binary can faithfully run) and picks the highest cached
// MINOR within it — instead of blindly latest-in-cache, where a stray download of a
// different major would silently become the default. These tests lock the underlying
// SelectArtifactVersionDir prefix contract that the default relies on, using synthetic
// version-named directories (no real artifacts needed).

using Xunit;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class BcArtifactSelectionTests : IDisposable
{
    private readonly string _root;

    public BcArtifactSelectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-artifact-sel", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void MajorPrefix_PicksHighestMinorWithinThatMajor()
    {
        // Cache spans two majors; a stray higher major (29) must NOT win when major 28 is asked.
        MakeVersionDirs("27.5.46862.48827", "28.1.49838.50794", "28.2.50931.52786", "29.0.10000.10000");

        var chosen = BcArtifacts.SelectArtifactVersionDir(_root, "28");
        Assert.Equal("28.2.50931.52786", Path.GetFileName(chosen));

        var chosen27 = BcArtifacts.SelectArtifactVersionDir(_root, "27");
        Assert.Equal("27.5.46862.48827", Path.GetFileName(chosen27));
    }

    [Fact]
    public void MajorPrefix_DoesNotBleedIntoNeighbouringMajor()
    {
        // "2" must not match "28.x"/"27.x"; only an exact leading segment counts.
        MakeVersionDirs("27.5.46862.48827", "28.2.50931.52786");

        var ex = Assert.Throws<InvalidOperationException>(
            () => BcArtifacts.SelectArtifactVersionDir(_root, "2"));
        Assert.Contains("matches version '2'", ex.Message);
    }

    [Fact]
    public void UnmatchedMajor_ThrowsLoud_NamingAvailableVersions()
    {
        MakeVersionDirs("28.2.50931.52786");

        var ex = Assert.Throws<InvalidOperationException>(
            () => BcArtifacts.SelectArtifactVersionDir(_root, "26"));
        // Loud failure: names what IS available so a human/agent can act.
        Assert.Contains("28.2.50931.52786", ex.Message);
        Assert.Contains("Download it explicitly", ex.Message);
    }

    /// <summary>
    /// The default must prefer the engine's built MAJOR.MINOR, not merely its major.
    ///
    /// MEASURED 2026-07-29 on Pageworks (1076 tests, identical dep set and binary):
    ///   engine built for 28.1.49838.50794, selected 28.1.x  -> 1041 pass / 35 fail / 0 error
    ///   engine built for 28.1.49838.50794, selected 28.2.x  ->  996 pass / 77 fail / 3 error
    /// The 42 regressions are all "testpage-part — could not resolve this control to a
    /// subpage part". Major-only matching let that skew through silently, contradicting
    /// the project's own finding that the engine must match the target BC MINOR.
    /// </summary>
    [Fact]
    public void DefaultPrefix_PrefersEngineMajorMinor_WhenThatMinorIsCached()
    {
        // The engine's exact build is NOT cached, so major.minor is the tightest match
        // available — see DefaultPrefix_PrefersExactEngineBuild_* for the tighter tier.
        MakeVersionDirs("28.1.49838.50794", "28.2.50931.52786");

        var prefix = BcArtifacts.DefaultVersionPrefix(new Version("28.1.49838.49999"), _root);
        Assert.Equal("28.1", prefix);

        // And it must actually select the engine-matched minor, not the highest one.
        Assert.Equal("28.1.49838.50794",
            Path.GetFileName(BcArtifacts.SelectArtifactVersionDir(_root, prefix)));
    }

    /// <summary>
    /// Tighter than major.minor: when the engine's EXACT 4-part build is cached, that is
    /// the only artifact whose Microsoft.Dynamics.Nav.CodeAnalysis.dll carries the assembly
    /// version bin/ was compiled against. Two builds of the same BC minor ship different
    /// CodeAnalysis versions (28.1.49838.50794 -> 17.0.36.40629,
    /// 28.1.49838.53220 -> 17.0.39.53543), and the strong-named reference does not tolerate
    /// the skew: selecting the higher 28.1 build makes the run die at startup with
    /// FileLoadException 0x80131621 on Microsoft.Dynamics.Nav.CodeAnalysis before a single
    /// test executes. Highest-in-minor is therefore wrong whenever the built version is
    /// itself cached.
    /// </summary>
    [Fact]
    public void DefaultPrefix_PrefersExactEngineBuild_OverAHigherBuildOfTheSameMinor()
    {
        MakeVersionDirs("28.1.49838.50794", "28.1.49838.53220", "28.2.50931.52786");

        var prefix = BcArtifacts.DefaultVersionPrefix(new Version("28.1.49838.50794"), _root);
        Assert.Equal("28.1.49838.50794", prefix);

        // And the prefix must resolve back to that exact build, not the higher 28.1 one.
        Assert.Equal("28.1.49838.50794",
            Path.GetFileName(BcArtifacts.SelectArtifactVersionDir(_root, prefix)));
    }

    /// <summary>
    /// Fallback: if the engine's own minor is not cached, the major is still the right
    /// constraint — but this is a degraded configuration, so the caller must be told.
    /// </summary>
    [Fact]
    public void DefaultPrefix_FallsBackToMajor_WhenEngineMinorNotCached()
    {
        MakeVersionDirs("28.2.50931.52786", "28.3.60000.60000");

        var prefix = BcArtifacts.DefaultVersionPrefix(new Version("28.1.49838.50794"), _root);
        Assert.Equal("28", prefix);
    }

    /// <summary>Negative: no engine version → no opinion, caller handles it.</summary>
    [Fact]
    public void DefaultPrefix_IsNull_WhenEngineVersionUnknown()
    {
        MakeVersionDirs("28.1.49838.50794");
        Assert.Null(BcArtifacts.DefaultVersionPrefix(null, _root));
    }

    /// <summary>
    /// The built BC version must survive into the shipped assembly. Ncl.dll's own version
    /// is major.0.0.0 (Microsoft stamps only the major), so without this attribute the
    /// runner cannot tell which MINOR it was built for and artifact selection degrades to
    /// major-only — the defect that silently cost 45 passing tests on Pageworks.
    /// </summary>
    [Fact]
    public void EngineBuiltVersion_IsBakedIn_AndCarriesAMinor()
    {
        var built = BcArtifacts.EngineBuiltVersion();
        Assert.NotNull(built);
        // 4-part and meaningful — a major.0.0.0 value would mean we baked in the useless
        // Ncl assembly version instead of the csproj _BCVersion.
        Assert.True(built!.Build > 0 && built.Revision > 0,
            $"BcEngineVersion must be the full 4-part _BCVersion, got '{built}'.");
        Assert.Equal(BcArtifacts.EngineMajor(AppContext.BaseDirectory) ?? built.Major, built.Major);
    }

    /// <summary>
    /// Negative control documenting WHY EngineBuiltVersion exists: the engine DLL's own
    /// version carries no minor. If this ever starts failing, Microsoft changed their
    /// stamping and the baked-in attribute may no longer be needed.
    /// </summary>
    [Fact]
    public void NclAssemblyVersion_CarriesNoUsableMinor()
    {
        var ncl = BcArtifacts.EngineVersion(AppContext.BaseDirectory);
        if (ncl == null) return; // no engine in bin — nothing to assert
        Assert.True(ncl.Minor == 0 && ncl.Build <= 0,
            $"Ncl.dll now reports a detailed version ({ncl}) — revisit EngineBuiltVersion.");
    }

    [Fact]
    public void EngineVersion_ReflectsBinNclVersion_OrNullWhenAbsent()
    {
        Assert.Null(BcArtifacts.EngineVersion(_root));

        var live = BcArtifacts.EngineVersion(AppContext.BaseDirectory);
        // When an engine is present its version must agree with EngineMajor.
        if (live != null)
            Assert.Equal(BcArtifacts.EngineMajor(AppContext.BaseDirectory), live.Major);
    }

    [Fact]
    public void EngineMajor_ReflectsBinNclVersion_OrNullWhenAbsent()
    {
        // Empty dir → no Ncl.dll → null (used as the "can't derive" fallback signal).
        Assert.Null(BcArtifacts.EngineMajor(_root));

        // The running test's bin dir has the real engine Ncl → a positive major (28 today).
        var live = BcArtifacts.EngineMajor(AppContext.BaseDirectory);
        Assert.True(live is null or > 0,
            "EngineMajor should be null (no engine in bin) or a positive BC major.");
    }
}
