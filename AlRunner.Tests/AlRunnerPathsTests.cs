// AlRunnerPathsTests — the cross-platform home resolution that replaced the POSIX-only
// HOME lookup (which was null on Windows, silently disabling cache/artifact discovery).

using Xunit;
using AlRunner.Infrastructure;

namespace AlRunner.Tests;

public sealed class AlRunnerPathsTests
{
    [Fact]
    public void UserHome_IsNonEmpty_Rooted_And_Exists_OnEveryOS()
    {
        var home = AlRunnerPaths.UserHome;
        Assert.False(string.IsNullOrEmpty(home)); // the exact failure mode of POSIX HOME on Windows
        Assert.True(Path.IsPathRooted(home));
        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public void UserHome_MatchesUserProfileSpecialFolder()
    {
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AlRunnerPaths.UserHome);
    }

    // Issue #2114: when $HOME names a directory that does not exist,
    // Environment.GetFolderPath(SpecialFolder.UserProfile) silently returns "" instead of
    // throwing (verified empirically: HOME=/missing -> "", HOME=/existing-empty -> the real
    // path). Every consumer then does Path.Combine(home, ".local", "share", ...), and
    // Path.Combine("", "a", "b") == "a/b" — a bare RELATIVE path with no leading separator.
    // That relative path survives every File.Exists/Directory.Exists probe downstream by
    // silently resolving against the CWD, and only fails deep inside
    // AssemblyLoadContext.LoadFromAssemblyPath (one of the few APIs that demands an
    // absolute path) — by which point it's an unhandled exception that aborts the process
    // with SIGABRT and a core dump instead of a diagnostic.
    //
    // AlRunnerPaths.Validate is the internal, parameter-driven core of UserHome's rootedness
    // check — tested directly (rather than by mutating the real process $HOME, which is
    // shared mutable state every parallel test collection reads) so this pins the exact
    // wrong shape without racing anything else in this heavily-parallelized test process.

    [Fact]
    public void Validate_EmptyResolvedHome_ThrowsNamingRawHomeValueAndRequiringAbsolutePath()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AlRunnerPaths.Validate(resolvedHome: "", rawHomeEnvVar: "/tmp/al-runner-2114-missing-home"));

        Assert.Contains("/tmp/al-runner-2114-missing-home", ex.Message);
        Assert.Contains("HOME", ex.Message);
        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Points at the documented explicit override for the artifact-root case specifically.
        Assert.Contains("--artifact-path", ex.Message);
    }

    [Fact]
    public void Validate_RelativeResolvedHome_Throws_EvenThoughFileExistsWouldSilentlyAcceptIt()
    {
        // The EXACT shape Path.Combine(home, BcArtifacts.ArtifactsRoot_Rel) produces when
        // home == "" — BcArtifacts.ArtifactsRoot's own construction. Reads the real
        // constant rather than spelling the path segments here (TestArtifactsGateTests'
        // OnlyTheSharedHelperNamesTheArtifactCachePathsInCode gate reserves that to
        // TestArtifacts.cs) — this is the same string either way.
        var relativeArtifactsRoot = Path.Combine("", BcArtifacts.ArtifactsRoot_Rel);
        Assert.False(Path.IsPathRooted(relativeArtifactsRoot));

        var ex = Assert.Throws<InvalidOperationException>(
            () => AlRunnerPaths.Validate(resolvedHome: relativeArtifactsRoot, rawHomeEnvVar: "/tmp/al-runner-2114-missing-home"));
        Assert.Contains("absolute", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_UnsetHome_MessageSaysUnset_NotABlankQuotedValue()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AlRunnerPaths.Validate(resolvedHome: "", rawHomeEnvVar: null));

        Assert.Contains("<unset>", ex.Message);
    }

    [Fact]
    public void Validate_RootedExistingHome_ReturnsItUnchanged_NeverThrows()
    {
        var abs = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        Assert.Equal(abs, AlRunnerPaths.Validate(resolvedHome: abs, rawHomeEnvVar: abs));
    }
}
