// WatchSiblingDependentBaselineTests — issue #4125, the cycles AFTER the one where the dependency moved.
//
// A dependent's incremental baseline replays any of its files that did not change, so it is only
// safe while the surface it was COMPILED against is the surface the dependency has now. Recording
// "the dependency moved this cycle" loses that the moment the forced compile fails or is skipped;
// these tests drive the two ordinary ways that happens, then edit the dependent once more.
//
//   P4  the forced compile fails (AL0132 in two files), the user fixes only ONE of them
//   P1  the forced compile is answered by an AL-output cache HIT (an undo), then the dependent is edited
//   PK  the same P4 shape with the dependency as a second bundle (the package path, #2683)
using Xunit;

namespace AlRunner.Tests;

public class WatchSiblingDependentBaselineTests
{
    private const string DepAppId = "41250000-0000-4000-8000-0000000f0001";
    private const string TestAppId = "41250000-0000-4000-8000-0000000f0002";
    private const string TestsRebuild = "[watch] RP Tests: FULL REBUILD";

    private static void Apps(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "dep"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "dep", "app.json"), $$"""
        { "id": "{{DepAppId}}", "name": "RP Dep", "publisher": "AL Runner", "version": "1.0.0.0", "dependencies": [],
          "idRanges": [ { "from": 64260, "to": 64264 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(root, "tests", "app.json"), $$"""
        { "id": "{{TestAppId}}", "name": "RP Tests", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [ { "id": "{{DepAppId}}", "name": "RP Dep", "publisher": "AL Runner", "version": "1.0.0.0" } ],
          "idRanges": [ { "from": 64265, "to": 64269 } ], "runtime": "14.0" }
        """);
    }

    private static void WriteExtraDep(string root, bool extra) =>
        WatchEdit.Replace(Path.Combine(root, "dep", "Answer.Codeunit.al"),
            "codeunit 64260 \"RP Answer\"\n{\n    procedure Value(): Integer\n    begin\n        exit(42);\n    end;\n"
            + (extra ? "    procedure Extra(): Integer\n    begin\n        exit(58);\n    end;\n" : "") + "}\n");

    // Two files in the dependent call Extra(): Helper always does, the test only while callsExtra.
    private static void WriteTwoCallers(string root, bool callsExtra)
    {
        WatchEdit.Replace(Path.Combine(root, "tests", "Helper.Codeunit.al"),
            "codeunit 64266 \"RP Helper\"\n{\n    procedure Call(): Integer\n    var\n        A: Codeunit \"RP Answer\";\n    begin\n        exit(A.Extra());\n    end;\n}\n");
        WatchEdit.Replace(Path.Combine(root, "tests", "T.Codeunit.al"),
            "codeunit 64265 \"RP Tests\"\n{\n    Subtype = Test;\n    [Test]\n    procedure Sum()\n    var\n        A: Codeunit \"RP Answer\";\n        H: Codeunit \"RP Helper\";\n    begin\n        if A.Value() + H.Call()"
            + (callsExtra ? " + A.Extra() - A.Extra()" : "") + " <> 100 then Error('RP sum %1', A.Value() + H.Call());\n    end;\n}\n");
    }

    private static async Task AssertFixingOneOfTwoCallersStillReportsTheOther(
        WatchCycleSession session, Action dropExtra, Action fixTestOnly, Action fixHelper)
    {
        var c1 = await session.Cycle(null);
        Assert.True(c1.Contains("PASS") && !c1.Contains("FAIL"), "cycle 1 did not pass:\n" + c1);

        var c2 = await session.Cycle(dropExtra);
        Assert.DoesNotContain("Function ID", c2);
        Assert.True(c2.Contains("AL0132") && c2.Contains(TestsRebuild), "cycle 2 did not report AL0132 after a rebuild:\n" + c2);

        // Only the test stops calling Extra(): Helper still does, and the dependency has not changed
        // since cycle 2. The cycle-2 compile failed, so nothing it left behind may be replayed.
        var c3 = await session.Cycle(fixTestOnly);
        Assert.DoesNotContain("Function ID", c3);
        Assert.True(c3.Contains("AL0132") && c3.Contains("Helper.Codeunit.al"),
            "cycle 3 did not report AL0132 for the caller that was not fixed:\n" + c3);

        var c4 = await session.Cycle(fixHelper);
        Assert.DoesNotContain("AL0132", c4);
        Assert.DoesNotContain("FAIL", c4);
        Assert.True(c4.Contains("PASS"), "cycle 4 did not pass once both callers were fixed:\n" + c4);
    }

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
        try
        {
            await AssertFixingOneOfTwoCallersStillReportsTheOther(session,
                dropExtra: () => WriteExtraDep(root, false),
                fixTestOnly: () => WriteTwoCallers(root, callsExtra: false),
                fixHelper: () => WatchEdit.Replace(Path.Combine(root, "tests", "Helper.Codeunit.al"),
                    "codeunit 64266 \"RP Helper\"\n{\n    procedure Call(): Integer\n    begin\n        exit(58);\n    end;\n}\n"));
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
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
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

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
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

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
        try
        {
            await AssertFixingOneOfTwoCallersStillReportsTheOther(session,
                dropExtra: () => WriteExtraDep(root, false),
                fixTestOnly: () => WriteTwoCallers(root, callsExtra: false),
                fixHelper: () => WatchEdit.Replace(Path.Combine(root, "tests", "Helper.Codeunit.al"),
                    "codeunit 64266 \"RP Helper\"\n{\n    procedure Call(): Integer\n    begin\n        exit(58);\n    end;\n}\n"));
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
