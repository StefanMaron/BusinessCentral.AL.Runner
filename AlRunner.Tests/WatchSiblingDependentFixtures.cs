// Shared setup for the WatchSiblingDependent* / WatchPackageDependent* classes (issue #4125): one dependency app
// ("RP Dep") and one test app ("RP Tests") that depends on it, under one parent directory.
using Xunit;

namespace AlRunner.Tests;

internal static class WatchSiblingDependentFixtures
{
    internal const string DepAppId = "41250000-0000-4000-8000-0000000f0001";
    internal const string TestAppId = "41250000-0000-4000-8000-0000000f0002";
    internal const string TestsRebuild = "[watch] RP Tests: FULL REBUILD";

    internal static void Apps(string root)
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

    internal static void WriteExtraDep(string root, bool extra) =>
        WatchEdit.Replace(Path.Combine(root, "dep", "Answer.Codeunit.al"),
            "codeunit 64260 \"RP Answer\"\n{\n    procedure Value(): Integer\n    begin\n        exit(42);\n    end;\n"
            + (extra ? "    procedure Extra(): Integer\n    begin\n        exit(58);\n    end;\n" : "") + "}\n");

    // Two files in the dependent call Extra(): Helper always does, the test only while callsExtra.
    internal static void WriteTwoCallers(string root, bool callsExtra)
    {
        WatchEdit.Replace(Path.Combine(root, "tests", "Helper.Codeunit.al"),
            "codeunit 64266 \"RP Helper\"\n{\n    procedure Call(): Integer\n    var\n        A: Codeunit \"RP Answer\";\n    begin\n        exit(A.Extra());\n    end;\n}\n");
        WatchEdit.Replace(Path.Combine(root, "tests", "T.Codeunit.al"),
            "codeunit 64265 \"RP Tests\"\n{\n    Subtype = Test;\n    [Test]\n    procedure Sum()\n    var\n        A: Codeunit \"RP Answer\";\n        H: Codeunit \"RP Helper\";\n    begin\n        if A.Value() + H.Call()"
            + (callsExtra ? " + A.Extra() - A.Extra()" : "") + " <> 100 then Error('RP sum %1', A.Value() + H.Call());\n    end;\n}\n");
    }

    internal static void FixHelper(string root) =>
        WatchEdit.Replace(Path.Combine(root, "tests", "Helper.Codeunit.al"),
            "codeunit 64266 \"RP Helper\"\n{\n    procedure Call(): Integer\n    begin\n        exit(58);\n    end;\n}\n");

    /// <summary>P4: the dependency drops Extra() (AL0132 in two files), then only ONE caller is fixed.</summary>
    internal static async Task AssertFixingOneOfTwoCallersStillReportsTheOther(WatchCycleSession session, string root)
    {
        var c1 = await session.Cycle(null);
        Assert.True(c1.Contains("PASS") && !c1.Contains("FAIL"), "cycle 1 did not pass:\n" + c1);

        var c2 = await session.Cycle(() => WriteExtraDep(root, false));
        Assert.DoesNotContain("Function ID", c2);
        Assert.True(c2.Contains("AL0132") && c2.Contains(TestsRebuild), "cycle 2 did not report AL0132 after a rebuild:\n" + c2);

        // Only the test stops calling Extra(): Helper still does, and the dependency has not changed
        // since cycle 2. The cycle-2 compile failed, so nothing it left behind may be replayed.
        var c3 = await session.Cycle(() => WriteTwoCallers(root, callsExtra: false));
        Assert.DoesNotContain("Function ID", c3);
        Assert.True(c3.Contains("AL0132") && c3.Contains("Helper.Codeunit.al"),
            "cycle 3 did not report AL0132 for the caller that was not fixed:\n" + c3);

        var c4 = await session.Cycle(() => FixHelper(root));
        Assert.DoesNotContain("AL0132", c4);
        Assert.DoesNotContain("FAIL", c4);
        Assert.True(c4.Contains("PASS"), "cycle 4 did not pass once both callers were fixed:\n" + c4);
    }

    internal static void Cleanup(params string[] dirs)
    {
        foreach (var d in dirs) { try { Directory.Delete(d, recursive: true); } catch { } }
    }
}
