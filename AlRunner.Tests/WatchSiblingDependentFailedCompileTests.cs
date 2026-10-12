// WatchSiblingDependentFailedCompileTests — issue #4125: a dependent whose forced compile FAILED is never replayed from the baseline of the compile before it.
using Xunit;
using static AlRunner.Tests.WatchSiblingDependentFixtures;

namespace AlRunner.Tests;

public class WatchSiblingDependentFailedCompileTests
{
    [SkippableFact]
    public async Task Watch_AFailedForcedCompile_IsNeverReplayedAfterAPartialFix()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-watch-baseline-p4");
        var cacheDir = TestScratch.Dir("al-runner-watch-baseline-p4-cache");
        Apps(root);
        WriteExtraDep(root, true);
        WriteTwoCallers(root, callsExtra: true);

        using var session = new WatchCycleSession($"\"{root}\"", cacheDir);
        try { await AssertFixingOneOfTwoCallersStillReportsTheOther(session, root); }
        finally { session.Dispose(); Cleanup(root, cacheDir); }
    }

    // The undo of a failed forced compile: the dependency is restored, so the terms are the ones the
    // baseline was last SUCCESSFULLY built against, and the failed compile in between must not have
    // replaced what is replayed.
    [SkippableFact]
    public async Task Watch_AFailedForcedCompile_ThenTheDependencyIsRestored_PassesAgain()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-watch-baseline-undo");
        var cacheDir = TestScratch.Dir("al-runner-watch-baseline-undo-cache");
        Apps(root);
        WriteExtraDep(root, true);
        WriteTwoCallers(root, callsExtra: true);

        using var session = new WatchCycleSession($"\"{root}\"", cacheDir);
        try
        {
            var c1 = await session.Cycle(null);
            Assert.True(c1.Contains("PASS") && !c1.Contains("FAIL"), "cycle 1 did not pass:\n" + c1);
            var c2 = await session.Cycle(() => WriteExtraDep(root, false));
            Assert.True(c2.Contains("AL0132"), "cycle 2 did not report AL0132:\n" + c2);
            var c3 = await session.Cycle(() => WriteExtraDep(root, true));
            Assert.DoesNotContain("AL0132", c3);
            Assert.DoesNotContain("FAIL", c3);
            Assert.True(c3.Contains("PASS"), "cycle 3 did not pass once the dependency was restored:\n" + c3);
        }
        finally { session.Dispose(); Cleanup(root, cacheDir); }
    }
}
