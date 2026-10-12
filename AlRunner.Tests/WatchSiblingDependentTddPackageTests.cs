// WatchSiblingDependentTddPackageTests — issue #4125 under --tdd. A compile that registered a package table
// extension records no RAD baseline and leaves the previous one in place (#5430), so "this app's compile
// succeeded" is not "the baseline now describes it". The sibling memory must be recorded only when the
// baseline was really replaced, or the next cycle replays the OLD baseline against the new surface.
//
//   c1  start
//   c2  the dependency changes P(Integer) to P(Decimal) and the test uses a package field that does not exist
//       yet (--tdd generates it, which is what keeps the baseline from being replaced)
//   c3  the test drops that use: the dependent must rebuild, not replay the baseline from before c2
//   c4  another test edit: incremental again
// The control runs the same cycles without the package field, where the baseline IS replaced.
using Xunit;

namespace AlRunner.Tests;

public class WatchSiblingDependentTddPackageTests
{
    private const string DepId = "41250000-0000-4000-8000-0000000fa001";
    private const string TestId = "41250000-0000-4000-8000-0000000fa002";
    private const string Rebuild = "[watch] RQ Tests: FULL REBUILD";

    private static void Apps(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "dep"));
        Directory.CreateDirectory(Path.Combine(root, "tests", ".alpackages"));
        File.WriteAllText(Path.Combine(root, "dep", "app.json"), $$"""
        { "id": "{{DepId}}", "name": "RQ Dep", "publisher": "AL Runner", "version": "1.0.0.0", "dependencies": [],
          "idRanges": [ { "from": 64270, "to": 64274 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(root, "tests", "app.json"), $$"""
        { "id": "{{TestId}}", "name": "RQ Tests", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [ { "id": "{{DepId}}", "name": "RQ Dep", "publisher": "AL Runner", "version": "1.0.0.0" },
            { "id": "{{TddPackageMemberTests.DepAppId}}", "name": "Tdd Package Members Dep", "publisher": "AL Runner Fixtures", "version": "1.0.0.0" } ],
          "idRanges": [ { "from": 64275, "to": 64279 } ], "runtime": "14.0" }
        """);
        File.WriteAllBytes(Path.Combine(root, "tests", ".alpackages", TddPackageMemberTests.DepFileStem + ".app"),
            TddPackageMemberTests.BuildApp(TddPackageMemberTests.DepAppId, "Tdd Package Members Dep", 65380, 65389,
                TddPackageMemberTests.DepSymbols, Path.Combine(TddPackageMemberTests.FixtureRoot, "dep")));
        File.WriteAllText(Path.Combine(root, "tests", "Helper.Codeunit.al"),
            "codeunit 64276 \"RQ Helper\"\n{\n    procedure Call(): Integer\n    var\n        A: Codeunit \"RQ Answer\";\n    begin\n        exit(A.P(5));\n    end;\n}\n");
    }

    private static void WriteDep(string root, string parameterType) =>
        WatchEdit.Replace(Path.Combine(root, "dep", "Answer.Codeunit.al"),
            "codeunit 64270 \"RQ Answer\"\n{\n    procedure P(X: " + parameterType + "): Integer\n    begin\n        exit(7);\n    end;\n}\n");

    private static void WriteTests(string root, int edit, bool packageField = false)
    {
        var markers = string.Concat(Enumerable.Range(2, Math.Max(0, edit - 1)).Select(n =>
            $"\n    [Test]\n    procedure Marker_Edit{n}()\n    begin\n    end;\n"));
        var field = packageField
            ? "\n    [Test]\n    procedure Points_RoundTrip()\n    var\n        E: Record \"Loyalty Entry\";\n    begin\n        E.Id := 1;\n        E.Points := 5;\n        E.Insert();\n        E.Get(1);\n        if E.Points <> 5 then Error('pts');\n    end;\n"
            : "";
        WatchEdit.Replace(Path.Combine(root, "tests", "T.Codeunit.al"),
            "codeunit 64275 \"RQ Tests\"\n{\n    Subtype = Test;\n    TestPermissions = Disabled;\n    [Test]\n    procedure Calls()\n    var\n        H: Codeunit \"RQ Helper\";\n    begin\n        if H.Call() <> 7 then Error('RQ got %1', H.Call());\n    end;\n"
            + markers + field + "}\n");
    }

    // --tdd prints "synthetic FAILED tests" in its own notices, so a bare "FAIL" would match them.
    private static bool Failed(string cycle) =>
        System.Text.RegularExpressions.Regex.IsMatch(cycle, @"^(FAIL|ERROR)\s", System.Text.RegularExpressions.RegexOptions.Multiline);

    private static async Task Run(string name, bool packageField)
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-watch-tdd-" + name);
        var cacheDir = TestScratch.Dir("al-runner-watch-tdd-" + name + "-cache");
        Apps(root);
        WriteDep(root, "Integer");
        WriteTests(root, 1);

        using var session = new WatchCycleSession($"\"{root}\" --tdd", cacheDir);
        try
        {
            var c1 = await session.Cycle(null);
            Assert.True(c1.Contains("PASS") && !Failed(c1), "cycle 1 did not pass:\n" + c1);

            var c2 = await session.Cycle(() => { WriteDep(root, "Decimal"); WriteTests(root, 2, packageField); });
            Assert.True(c2.Contains(Rebuild) && c2.Contains("PASS") && !Failed(c2), "cycle 2:\n" + c2);
            if (packageField) Assert.Contains("Points", c2);

            var c3 = await session.Cycle(() => WriteTests(root, 3));
            Assert.DoesNotContain("Function ID", c3);
            Assert.True(c3.Contains("PASS") && !Failed(c3), "cycle 3:\n" + c3);
            // With the package field the cycle-2 compile did not replace the baseline, so this rebuilds;
            // without it the baseline was replaced and this stays incremental.
            Assert.Equal(packageField, c3.Contains(Rebuild));

            var c4 = await session.Cycle(() => WriteTests(root, 4));
            Assert.DoesNotContain("Function ID", c4);
            Assert.True(c4.Contains("PASS") && !Failed(c4), "cycle 4:\n" + c4);
            Assert.DoesNotContain(Rebuild, c4);
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public Task Watch_Tdd_ACompileThatKeptTheOldBaseline_IsNotRecordedAsBuiltAgainstTheNewSurface() => Run("package", packageField: true);

    [SkippableFact]
    public Task Watch_Tdd_WithoutAPackageField_TheBaselineIsReplacedAndStaysIncremental() => Run("control", packageField: false);
}
