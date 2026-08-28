// PrecompileSupport — small, pure helpers for the `--precompile` subcommand (issue #2131).
//
// Moved out of Program.cs's top-level-statement local functions for the same reason
// WatchSource.cs was (#1822): a local function declared inside top-level statements is
// nested inside the synthesized <Main>$ method and cannot be referenced from another
// file/class at all, so it has no way to be unit-tested directly. AlRunner.csproj already
// grants AlRunner.Tests InternalsVisibleTo, so an `internal` class here is directly
// testable — no new plumbing required.
namespace AlRunner;

internal static class PrecompileSupport
{
    /// <summary>
    /// Always folds the SELECTED version's own runner-owned platform-apps/test-apps dirs
    /// (the exact <c>&lt;artifacts-root&gt;/&lt;version&gt;/{platform-apps,test-apps}</c> this
    /// runner itself downloads into via <c>provision</c>/--auto-provision) into a
    /// caller-supplied package-cache dir list, when present on disk — mirrors the main
    /// bundle-run flow's runnerOwnedPlatformAppsDir/runnerOwnedTestAppsDir fold-in (issue
    /// #1996), which closes this same gap for an EXPLICIT --package-cache there.
    /// --precompile's own packageCacheDirs computation never had that fold-in, which is
    /// how a Tier-3 compile of a Microsoft test-toolkit app (Library Assert, whose own
    /// NavxManifest.xml &lt;Dependencies&gt; is empty — its need for System Application is
    /// via the implicit <c>Platform=</c> root, not an explicit dependency edge) could miss
    /// System Application/Application Test Library/PEPPOL even though they were sitting
    /// right next to test-apps the whole time (issue #2131: AL1022 for all three, cascading
    /// into AL0791 "namespace 'Reflection' is unknown" and two AL0185 "Table 'Field' is
    /// missing", all from the SAME missing-symbols cause).
    ///
    /// Pure — does no mutation of <paramref name="baseDirs"/>, and only ever ADDS a dir
    /// that genuinely exists on disk, so it can never turn a working search set into a
    /// broken one. Plain string list in/out so tests can prove it without touching
    /// BcArtifacts' process-global state at all.
    /// </summary>
    internal static List<string> WidenPackageCacheDirs(
        IReadOnlyList<string> baseDirs, string artifactsRootDir, string selectedVersion)
    {
        var result = baseDirs.ToList();
        foreach (var d in new[]
        {
            AlRunner.Infrastructure.ProvisioningCheck.PlatformAppsDirFor(artifactsRootDir, selectedVersion),
            AlRunner.Infrastructure.ProvisioningCheck.TestAppsDirFor(artifactsRootDir, selectedVersion),
        })
        {
            if (Directory.Exists(d) && !result.Contains(d))
                result.Add(d);
        }
        return result;
    }
}
