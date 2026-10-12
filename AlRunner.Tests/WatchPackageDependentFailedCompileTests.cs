// WatchPackageDependentFailedCompileTests — issue #4125, the package path (#2683): the same P4 shape with the dependency passed as a second bundle.
using Xunit;
using static AlRunner.Tests.WatchSiblingDependentFixtures;

namespace AlRunner.Tests;

public class WatchPackageDependentFailedCompileTests
{
    // The dependency as a second bundle: RunLayeredPrePass hands the dependent a synthesised package
    // instead of a sibling's symbols, and changedImplAppIds reports the move for one cycle only.
    [SkippableFact]
    public async Task Watch_PackageDependency_AFailedForcedCompile_IsNeverReplayedAfterAPartialFix()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-watch-baseline-pk");
        var cacheDir = TestScratch.Dir("al-runner-watch-baseline-pk-cache");
        Apps(root);
        WriteExtraDep(root, true);
        WriteTwoCallers(root, callsExtra: true);

        using var session = new WatchCycleSession($"\"{Path.Combine(root, "dep")}\" \"{Path.Combine(root, "tests")}\"", cacheDir);
        try { await AssertFixingOneOfTwoCallersStillReportsTheOther(session, root); }
        finally { session.Dispose(); Cleanup(root, cacheDir); }
    }
}
