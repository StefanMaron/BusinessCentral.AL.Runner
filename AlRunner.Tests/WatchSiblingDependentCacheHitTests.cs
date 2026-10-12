// WatchSiblingDependentCacheHitTests — issue #4125: an AL-output cache HIT in place of the forced compile leaves the incremental baseline on the old surface; the next edit must not replay it.
using Xunit;
using static AlRunner.Tests.WatchSiblingDependentFixtures;

namespace AlRunner.Tests;

public class WatchSiblingDependentCacheHitTests
{
    [SkippableFact]
    public async Task Watch_ACacheHitInPlaceOfTheForcedCompile_DoesNotLeaveTheBaselineOnTheOldSurface()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-watch-baseline-p1");
        var cacheDir = TestScratch.Dir("al-runner-watch-baseline-p1-cache");
        Apps(root);
        void Dep(string parameterType) => WatchEdit.Replace(Path.Combine(root, "dep", "Answer.Codeunit.al"), $$"""
            codeunit 64260 "RP Answer"
            {
                procedure P(X: {{parameterType}}): Integer
                begin
                    exit(7);
                end;
            }
            """);
        File.WriteAllText(Path.Combine(root, "tests", "Helper.Codeunit.al"), """
            codeunit 64266 "RP Helper"
            {
                procedure Call(): Integer
                var
                    A: Codeunit "RP Answer";
                begin
                    exit(A.P(5));
                end;
            }
            """);
        void Tests(int edit) => WatchEdit.Replace(Path.Combine(root, "tests", "T.Codeunit.al"), $$"""
            codeunit 64265 "RP Tests"
            {
                Subtype = Test;
                [Test]
                procedure Calls()
                var
                    H: Codeunit "RP Helper";
                begin
                    if H.Call() <> 7 then Error('RP got %1', H.Call());
                end;
            {{string.Concat(Enumerable.Range(2, Math.Max(0, edit - 1)).Select(n => $"\n    [Test]\n    procedure Marker_Edit{n}()\n    begin\n    end;\n"))}}
            }
            """);
        Dep("Integer");
        Tests(1);

        using var session = new WatchCycleSession($"\"{root}\"", cacheDir);
        try
        {
            var c1 = await session.Cycle(null);
            Assert.True(c1.Contains("PASS") && !c1.Contains("FAIL"), "cycle 1 did not pass:\n" + c1);

            var c2 = await session.Cycle(() => Dep("Decimal"));
            Assert.True(c2.Contains(TestsRebuild) && c2.Contains("PASS") && !c2.Contains("FAIL"), "cycle 2:\n" + c2);

            // Back to the surface cycle 1 published a cache entry for: the dependent is served from it.
            var c3 = await session.Cycle(() => Dep("Integer"));
            Assert.True(c3.Contains("PASS") && !c3.Contains("FAIL"), "cycle 3 (the undo) did not pass:\n" + c3);

            // Edit only the dependent. Its baseline was last compiled against the Decimal surface.
            var c4 = await session.Cycle(() => Tests(2));
            Assert.DoesNotContain("Function ID", c4);
            Assert.True(c4.Contains("Marker_Edit2") && c4.Contains("PASS") && !c4.Contains("FAIL"), "cycle 4:\n" + c4);
        }
        finally { session.Dispose(); Cleanup(root, cacheDir); }
    }
}
