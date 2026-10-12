// WatchSiblingDependentStaleTests — issue #4125.
//
// A parent-of-many-apps bundle: a dependency app and a test app that depends on it. WatchSibling-
// SymbolsIncrementalTests (#2672) edits BOTH apps every cycle, so the dependent's own files always
// change. Here the dependency changes ALONE: the dependent's files are byte-identical, which is the
// only case where "my files are unchanged" can be wrong about "my compile is unchanged".
//
// The session is one process, one cycle per phase:
//   c1  start, passes
//   c2  edit ONLY the dependent                   -> incremental path still taken (the control)
//   c3  dependency loses a procedure the dependent calls -> AL0132, not a run-time Function ID error
//   c4  dependency gains it back                  -> compiles and passes again
//   c5  dependency body edit, surface unchanged   -> the dependent sees the new behaviour, no rebuild
//   c6  [Obsolete] on a procedure, c7 Access = Internal -> the symbols carry both, so both rebuild it
using Xunit;

namespace AlRunner.Tests;

public class WatchSiblingDependentStaleTests
{
    private const string DepAppId = "41250000-0000-4000-8000-00000000d001";
    private const string TestAppId = "41250000-0000-4000-8000-00000000d002";
    private const string DepLine = "[sibling-symbols] WSI Sibling Dep 1.0.0.0: ";
    private const string TestsRebuildLine = "[watch] WSI Sibling Tests: FULL REBUILD";

    internal static void WriteApps(string root)
    {
        var depDir = Path.Combine(root, "wsi-dep");
        var testDir = Path.Combine(root, "wsi-tests");
        Directory.CreateDirectory(depDir);
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{DepAppId}}", "name": "WSI Sibling Dep", "publisher": "AL Runner",
          "version": "1.0.0.0", "dependencies": [],
          "idRanges": [ { "from": 64060, "to": 64064 } ], "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
        {
          "id": "{{TestAppId}}", "name": "WSI Sibling Tests", "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ { "id": "{{DepAppId}}", "name": "WSI Sibling Dep", "publisher": "AL Runner", "version": "1.0.0.0" } ],
          "idRanges": [ { "from": 64065, "to": 64069 } ], "runtime": "14.0"
        }
        """);
    }

    internal static void WriteDep(string root, int? extraAnswer, string extraAttribute = "", string codeunitProperty = "")
    {
        var extra = extraAnswer is { } n
            ? $"\n    {extraAttribute}\n    procedure Extra(): Integer\n    begin\n        exit({n});\n    end;\n"
            : "";
        WatchEdit.Replace(Path.Combine(root, "wsi-dep", "Answer.Codeunit.al"), $$"""
        codeunit 64060 "WSI Answer"
        {
            {{codeunitProperty}}
            procedure Value(): Integer
            begin
                exit(42);
            end;
        {{extra}}
        }
        """);
    }

    // Edit N adds a marker test, and --show-pass prints it, so the output says which version of
    // this file a cycle compiled (#4707). Procedures are only ever ADDED: removing or renaming one
    // is a different full-rebuild reason (#5102) and would blur what c2 controls for.
    internal static void WriteTests(string root, int edit)
    {
        var markers = string.Concat(Enumerable.Range(2, Math.Max(0, edit - 1)).Select(n =>
            $"\n    [Test]\n    procedure Marker_Edit{n}()\n    begin\n    end;\n"));
        WatchEdit.Replace(Path.Combine(root, "wsi-tests", "AnswerTests.Codeunit.al"), $$"""
        codeunit 64065 "WSI Answer Tests"
        {
            Subtype = Test;

            [Test]
            procedure AnswerMatches()
            var
                Answer: Codeunit "WSI Answer";
            begin
                if Answer.Value() + Answer.Extra() <> 100 then
                    Error('WSI answered %1, expected 100', Answer.Value() + Answer.Extra());
            end;
        {{markers}}
        }
        """);
    }

    [SkippableFact]
    public async Task Watch_SiblingDependencyAloneChanges_TheUnchangedDependentIsRecompiledAgainstIt()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-watch-sibling-dependent-4125");
        var cacheDir = TestScratch.Dir("al-runner-watch-sibling-dependent-4125-cache");
        WriteApps(root);
        WriteDep(root, 58);
        WriteTests(root, 1);

        using var session = new WatchCycleSession($"\"{root}\"", cacheDir);
        try
        {
            var c1 = await session.Cycle(null);
            Assert.True(c1.Contains("PASS") && c1.Contains("AnswerMatches"), "cycle 1 did not pass:\n" + c1);
            Assert.DoesNotContain("FAIL", c1);

            // c2, the control: only the dependent changes, so its incremental baseline is still taken.
            var c2 = await session.Cycle(() => WriteTests(root, 2));
            Assert.True(c2.Contains("Marker_Edit2") && c2.Contains("PASS"), "cycle 2 did not run the edited test:\n" + c2);
            Assert.DoesNotContain("FAIL", c2);
            Assert.DoesNotContain(TestsRebuildLine, c2);
            Assert.True(c2.Contains(DepLine + "RAD incremental (fast path)"),
                "cycle 2 recompiled the unchanged sibling:\n" + c2);

            // c3: the dependency loses Extra() and the dependent is not touched. BC rejects the
            // dependent at compile time; the run-time Function ID failure is the stale-output symptom.
            var c3 = await session.Cycle(() => WriteDep(root, null));
            Assert.DoesNotContain("Function ID", c3);
            Assert.True(c3.Contains("AL0132"), "cycle 3 did not report AL0132 for the dependent:\n" + c3);
            Assert.True(c3.Contains(TestsRebuildLine) && c3.Contains("'WSI Sibling Dep'"),
                "cycle 3 did not say the dependent was rebuilt because the dependency changed:\n" + c3);

            // c4: the procedure is back, the dependent is still untouched: it must compile and pass.
            var c4 = await session.Cycle(() => WriteDep(root, 58));
            Assert.DoesNotContain("AL0132", c4);
            Assert.DoesNotContain("FAIL", c4);
            Assert.True(c4.Contains("PASS") && c4.Contains("Marker_Edit2"), "cycle 4 did not pass again:\n" + c4);
            Assert.True(c4.Contains(TestsRebuildLine), "cycle 4 did not rebuild the dependent:\n" + c4);

            // c5: a body-only edit keeps the surface, so the dependent is NOT rebuilt but still runs
            // the dependency's new code.
            var c5 = await session.Cycle(() => WriteDep(root, 59));
            Assert.True(c5.Contains("WSI answered 101"), "cycle 5 did not run the edited dependency:\n" + c5);
            Assert.DoesNotContain(TestsRebuildLine, c5);
            Assert.True(c5.Contains(DepLine + "RAD incremental (fast path)"),
                "cycle 5 did not take the fast path for the edited sibling:\n" + c5);

            // c6, c7: what the symbols carry as an attribute or a property is part of the surface the
            // dependent is compiled against, so changing either rebuilds it.
            var c6 = await session.Cycle(() => WriteDep(root, 59, extraAttribute: "[Obsolete('use Value', '1.0')]"));
            Assert.True(c6.Contains(TestsRebuildLine), "cycle 6 did not rebuild the dependent for an [Obsolete] attribute:\n" + c6);
            var c7 = await session.Cycle(() => WriteDep(root, 59, codeunitProperty: "Access = Internal;"));
            Assert.True(c7.Contains(TestsRebuildLine), "cycle 7 did not rebuild the dependent for Access = Internal:\n" + c7);
            Assert.DoesNotContain("WSI answered 101", c7);
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
