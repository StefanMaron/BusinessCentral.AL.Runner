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
