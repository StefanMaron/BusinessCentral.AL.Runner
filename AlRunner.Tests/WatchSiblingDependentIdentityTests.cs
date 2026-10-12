// WatchSiblingDependentIdentityTests — issue #4125, the key the "built against" record is kept under.
//
// The RAD baseline a dependent replays is keyed by its MODULE NAME (BcCompiler._radBaselines[moduleName]),
// so the record of what that baseline was built against has to be keyed by the name too. Keyed by the
// directory, a rename and a rename back reached the baseline the old name had left, with the record the
// other name had written:
//   c1  start (baseline under "RQ Tests", built against P(Integer))
//   c2  the app is renamed and a test edited (new name: no baseline, rebuild)
//   c3  the dependency changes P(Integer) to P(Decimal) (the renamed app rebuilds)
//   c4  renamed back, test file as in c1: the old name's baseline is built against P(Integer), so it must rebuild
//   c5  another test edit: incremental again
using Xunit;

namespace AlRunner.Tests;

public class WatchSiblingDependentIdentityTests
{
    private const string DepId = "41250000-0000-4000-8000-0000000fc001";
    private const string TestId = "41250000-0000-4000-8000-0000000fc002";

    private static void Manifest(string root, string dir, string id, string name, int from, string deps = "")
    {
        Directory.CreateDirectory(Path.Combine(root, dir));
        File.WriteAllText(Path.Combine(root, dir, "app.json"), $$"""
        { "id": "{{id}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0", "dependencies": [ {{deps}} ],
          "idRanges": [ { "from": {{from}}, "to": {{from + 4}} } ], "runtime": "14.0" }
        """);
    }

    private static readonly string DepRef =
        $$"""{ "id": "{{DepId}}", "name": "RQ Dep", "publisher": "AL Runner", "version": "1.0.0.0" }""";

    private static void WriteDep(string root, string parameterType) =>
        WatchEdit.Replace(Path.Combine(root, "dep", "Answer.Codeunit.al"),
            "codeunit 64270 \"RQ Answer\"\n{\n    procedure P(X: " + parameterType + "): Integer\n    begin\n        exit(7);\n    end;\n}\n");

    private static void WriteTests(string root, int edit)
    {
        var markers = string.Concat(Enumerable.Range(2, Math.Max(0, edit - 1)).Select(n =>
            $"\n    [Test]\n    procedure Marker_Edit{n}()\n    begin\n    end;\n"));
        WatchEdit.Replace(Path.Combine(root, "tests", "T.Codeunit.al"),
            "codeunit 64275 \"RQ Tests\"\n{\n    Subtype = Test;\n    [Test]\n    procedure Calls()\n    var\n        H: Codeunit \"RQ Helper\";\n    begin\n        if H.Call() <> 7 then Error('RQ got %1', H.Call());\n    end;\n"
            + markers + "}\n");
    }

    [SkippableFact]
    public async Task Watch_ADependentRenamedAndRenamedBack_IsNotReplayedAgainstTheSurfaceTheOtherNameSaw()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-watch-identity");
        var cacheDir = TestScratch.Dir("al-runner-watch-identity-cache");
        Manifest(root, "dep", DepId, "RQ Dep", 64270);
        Manifest(root, "tests", TestId, "RQ Tests", 64275, DepRef);
        WriteDep(root, "Integer");
        File.WriteAllText(Path.Combine(root, "tests", "Helper.Codeunit.al"),
            "codeunit 64276 \"RQ Helper\"\n{\n    procedure Call(): Integer\n    var\n        A: Codeunit \"RQ Answer\";\n    begin\n        exit(A.P(5));\n    end;\n}\n");
        WriteTests(root, 1);

        using var session = new WatchCycleSession($"\"{root}\"", cacheDir);
        try
        {
            var c1 = await session.Cycle(null);
            Assert.True(c1.Contains("PASS") && !c1.Contains("FAIL"), "cycle 1 did not pass:\n" + c1);

            var c2 = await session.Cycle(() =>
            {
                Manifest(root, "tests", TestId, "RQ TestsRenamed", 64275, DepRef);
                WriteTests(root, 2);
            });
            Assert.True(c2.Contains("PASS") && !c2.Contains("FAIL"), "cycle 2 (renamed) did not pass:\n" + c2);

            var c3 = await session.Cycle(() => WriteDep(root, "Decimal"));
            Assert.True(c3.Contains("[watch] RQ TestsRenamed: FULL REBUILD") && c3.Contains("PASS") && !c3.Contains("FAIL"),
                "cycle 3 (the dependency changed) did not rebuild the renamed app:\n" + c3);

            var c4 = await session.Cycle(() =>
            {
                Manifest(root, "tests", TestId, "RQ Tests", 64275, DepRef);
                WriteTests(root, 1);
            });
            Assert.DoesNotContain("Function ID", c4);
            Assert.True(c4.Contains("[watch] RQ Tests: FULL REBUILD") && c4.Contains("'RQ Dep'")
                && c4.Contains("PASS") && !c4.Contains("FAIL"), "cycle 4 (renamed back) did not rebuild against the new surface:\n" + c4);

            var c5 = await session.Cycle(() => WriteTests(root, 2));
            Assert.DoesNotContain("Function ID", c5);
            Assert.True(c5.Contains("PASS") && !c5.Contains("FAIL"), "cycle 5:\n" + c5);
            Assert.DoesNotContain("[watch] RQ Tests: FULL REBUILD", c5);
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
