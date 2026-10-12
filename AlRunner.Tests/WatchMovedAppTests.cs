// WatchMovedAppTests — issue #5520, an app whose folder moves while `--watch` is running.
//
// DependencyLoader.TryGetByAppId read the app at its new folder as another bundle's copy of the
// identity (#1683) and handed back the module compiled from the old folder, every cycle:
//   c1  start (dependency + dependent, one test)
//   c2  the dependent's folder moves (files copied, old folder deleted: a rename raises no .al event)
//   c3  a second [Test] is added in the new folder      -> it must run
//   c4  the dependency's P(Integer) becomes P(Decimal)  -> the moved app must rebuild against it
using Xunit;

namespace AlRunner.Tests;

public class WatchMovedAppTests
{
    private const string DepId = "52200000-0000-4000-8000-0000000fc001";
    private const string TestId = "52200000-0000-4000-8000-0000000fc002";

    private static void Manifest(string root, string dir, string id, string name, int from, string deps = "")
    {
        Directory.CreateDirectory(Path.Combine(root, dir));
        WatchEdit.Replace(Path.Combine(root, dir, "app.json"), $$"""
        { "id": "{{id}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0", "dependencies": [ {{deps}} ],
          "idRanges": [ { "from": {{from}}, "to": {{from + 4}} } ], "runtime": "14.0" }
        """);
    }

    private static readonly string DepRef =
        $$"""{ "id": "{{DepId}}", "name": "MV Dep", "publisher": "AL Runner", "version": "1.0.0.0" }""";

    private static void WriteDep(string root, string parameterType) =>
        WatchEdit.Replace(Path.Combine(root, "dep", "Answer.Codeunit.al"),
            "codeunit 64320 \"MV Answer\"\n{\n    procedure P(X: " + parameterType + "): Integer\n    begin\n        exit(7);\n    end;\n}\n");

    private static void WriteTests(string root, string dir, int tests)
    {
        var markers = string.Concat(Enumerable.Range(2, Math.Max(0, tests - 1)).Select(n =>
            $"\n    [Test]\n    procedure Marker_Edit{n}()\n    begin\n    end;\n"));
        WatchEdit.Replace(Path.Combine(root, dir, "T.Codeunit.al"),
            "codeunit 64325 \"MV Tests\"\n{\n    Subtype = Test;\n    [Test]\n    procedure Calls()\n    var\n        H: Codeunit \"MV Helper\";\n    begin\n        if H.Call() <> 7 then Error('MV got %1', H.Call());\n    end;\n"
            + markers + "}\n");
    }

    private static void MoveTests(string root, string from, string to)
    {
        Directory.CreateDirectory(Path.Combine(root, to));
        foreach (var file in Directory.GetFiles(Path.Combine(root, from)))
            WatchEdit.Replace(Path.Combine(root, to, Path.GetFileName(file)), File.ReadAllText(file));
        Directory.Delete(Path.Combine(root, from), recursive: true);
    }

    [SkippableFact]
    public async Task Watch_AnAppWhoseFolderMoves_IsRecompiledNotServedFromItsOldFolder()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-watch-moved");
        var cacheDir = TestScratch.Dir("al-runner-watch-moved-cache");
        Manifest(root, "dep", DepId, "MV Dep", 64320);
        Manifest(root, "tests", TestId, "MV Tests", 64325, DepRef);
        WriteDep(root, "Integer");
        File.WriteAllText(Path.Combine(root, "tests", "Helper.Codeunit.al"),
            "codeunit 64326 \"MV Helper\"\n{\n    procedure Call(): Integer\n    var\n        A: Codeunit \"MV Answer\";\n    begin\n        exit(A.P(5));\n    end;\n}\n");
        WriteTests(root, "tests", 1);

        using var session = new WatchCycleSession($"\"{root}\"", cacheDir);
        try
        {
            var c1 = await session.Cycle(null);
            Assert.True(c1.Contains("PASS") && !c1.Contains("FAIL"), "cycle 1 did not pass:\n" + c1);

            var c2 = await session.Cycle(() => MoveTests(root, "tests", "tests2"));
            Assert.DoesNotContain("reusing that module instead of recompiling", c2);
            Assert.True(c2.Contains("no longer holds this app") && c2.Contains("PASS") && !c2.Contains("FAIL"),
                "cycle 2 (the folder moved) did not say it recompiled, or did not pass:\n" + c2);

            var c3 = await session.Cycle(() => WriteTests(root, "tests2", 2));
            Assert.DoesNotContain("reusing that module instead of recompiling", c3);
            Assert.True(c3.Contains("Marker_Edit2") && !c3.Contains("FAIL"),
                "cycle 3 did not run the test added in the new folder:\n" + c3);

            var c4 = await session.Cycle(() => WriteDep(root, "Decimal"));
            Assert.DoesNotContain("Function ID", c4);
            Assert.True(c4.Contains("PASS") && !c4.Contains("FAIL") && c4.Contains("[watch] MV Tests: FULL REBUILD"),
                "cycle 4 (the dependency changed) did not rebuild the moved app:\n" + c4);
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
