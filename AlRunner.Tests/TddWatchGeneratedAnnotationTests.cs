// #5494: `--tdd --watch` after a cycle that generated a member. A compile that generated anything records no incremental baseline: the baseline would
// hold the trees WITH the generated text in them, and an incremental cycle runs no generation, so a test would keep running against the member while its
// annotation and the closing list said that none was generated. The next cycle is a full rebuild, which generates the member again and reports it.
// Runner-specific (the report --tdd prints), so it lives here, not in the al-language corpus. Spawns the real runner.
using Xunit;

namespace AlRunner.Tests;

public class TddWatchGeneratedAnnotationTests
{
    private const string AppId = "5a1b2c3d-0001-4a5b-8c6d-7e8f90a1b2c3";
    private const string Stub = "Local Entry: field \"Points\": Integer";

    private static void Write(string root, int edit)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        { "id": "{{AppId}}", "name": "Tdd Watch Annotation", "publisher": "AL Runner", "version": "1.0.0.0", "dependencies": [],
          "idRanges": [ { "from": 64290, "to": 64299 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(root, "Entry.Table.al"),
            "table 64290 \"Local Entry\"\n{\n    fields\n    {\n        field(1; Id; Integer) { }\n    }\n    keys\n    {\n        key(PK; Id) { Clustered = true; }\n    }\n}\n");
        File.WriteAllText(Path.Combine(root, "Points.Codeunit.al"),
            "codeunit 64291 \"Points Tests\"\n{\n    Subtype = Test;\n    TestPermissions = Disabled;\n\n    [Test]\n    procedure PointsRoundTrip()\n    var\n        E: Record \"Local Entry\";\n    begin\n" +
            "        E.Id := 1;\n        E.Points := 5;\n        E.Insert();\n        E.Get(1);\n        if E.Points <> 5 then Error('points');\n    end;\n}\n");
        WatchEdit.Replace(Path.Combine(root, "Other.Codeunit.al"),
            "codeunit 64292 \"Other Tests\"\n{\n    Subtype = Test;\n    TestPermissions = Disabled;\n\n    [Test]\n    procedure Unrelated()\n    begin\n    end;\n" +
            string.Concat(Enumerable.Range(2, Math.Max(0, edit - 1)).Select(n => $"\n    [Test]\n    procedure Marker_Edit{n}()\n    begin\n    end;\n")) + "}\n");
    }

    /// <summary>
    /// Cycle 1 generates the field "Points" of the app's own table and the test round-trips it. Cycle 2 edits a file that has nothing to do with it:
    /// the test still passes against the field, so the cycle names it again (the annotation on the test, the summary, the closing list), and the cycle is a
    /// full rebuild that says why. Cycle 3 is the same.
    /// </summary>
    [SkippableFact]
    public async Task AnEditElsewhere_NextCycle_StillNamesTheGeneratedField()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-tdd-watch-annotation");
        var cacheDir = TestScratch.Dir("al-runner-tdd-watch-annotation-cache");
        Write(root, 1);

        using var session = new WatchCycleSession($"\"{root}\" --tdd", cacheDir);
        try
        {
            void AssertNamesTheField(string cycle, string label)
            {
                Assert.True(cycle.Contains("PASS") && !System.Text.RegularExpressions.Regex.IsMatch(cycle, @"^(FAIL|ERROR)\s", System.Text.RegularExpressions.RegexOptions.Multiline),
                    $"{label} did not pass:\n{cycle}");
                Assert.Contains($"{TddReport.PerTestPrefix}{Stub}", cycle);
                Assert.Contains("--tdd: 1 test(s) reach generated stubs this run:", cycle);
                Assert.Contains("--tdd: generated 1 member(s) this run:", cycle);
                Assert.DoesNotContain("no members were generated", cycle);
            }

            var c1 = await session.Cycle(null);
            AssertNamesTheField(c1, "cycle 1");

            var c2 = await session.Cycle(() => Write(root, 2));
            AssertNamesTheField(c2, "cycle 2");
            Assert.Contains("FULL REBUILD", c2);
            Assert.Contains("generated a member or an object", c2);

            var c3 = await session.Cycle(() => Write(root, 3));
            AssertNamesTheField(c3, "cycle 3");
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
