// WatchSiblingDependentChainTests — issue #4125: the end of a three-level chain is rebuilt when the library at its start changes.
using Xunit;

namespace AlRunner.Tests;

public class WatchSiblingDependentChainTests
{
    // Lib <- Mid <- Tests. Tests declares Mid only, and Mid (which propagates its dependencies)
    // never calls what Lib loses, so Mid's own symbols do not move: only the chain tells Tests that
    // what Mid was compiled against did.
    [SkippableFact]
    public async Task Watch_ChainOfThree_ALibraryChangeReachesTheAppAtTheEndOfTheChain()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-watch-sibling-chain-4125");
        var cacheDir = TestScratch.Dir("al-runner-watch-sibling-chain-4125-cache");
        const string LibId = "41250000-0000-4000-8000-0000000001a1";
        const string MidId = "41250000-0000-4000-8000-0000000001b2";
        const string TestsId = "41250000-0000-4000-8000-0000000001c3";
        void App(string dir, string id, string name, int from, string extra, string deps)
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
            File.WriteAllText(Path.Combine(root, dir, "app.json"), $$"""
            { "id": "{{id}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0", {{extra}}
              "dependencies": [ {{deps}} ], "idRanges": [ { "from": {{from}}, "to": {{from + 4}} } ], "runtime": "14.0" }
            """);
        }
        string Dep(string id, string name) =>
            $$"""{ "id": "{{id}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0" }""";
        App("ss-lib", LibId, "SS Lib", 64080, "", "");
        App("ss-mid", MidId, "SS Mid", 64085, "\"propagateDependencies\": true,", Dep(LibId, "SS Lib"));
        App("ss-tests", TestsId, "SS Tests", 64090, "", Dep(MidId, "SS Mid"));
        void WriteLib(bool extra) => WatchEdit.Replace(Path.Combine(root, "ss-lib", "Lib.Codeunit.al"), $$"""
            codeunit 64080 "SS Lib"
            {
                procedure Value(): Integer
                begin
                    exit(42);
                end;
            {{(extra ? "\n    procedure Extra(): Integer\n    begin\n        exit(58);\n    end;\n" : "")}}
            }
            """);
        File.WriteAllText(Path.Combine(root, "ss-mid", "Mid.Codeunit.al"), """
            codeunit 64085 "SS Mid"
            {
                procedure Total(): Integer
                var
                    Lib: Codeunit "SS Lib";
                begin
                    exit(Lib.Value() + 1);
                end;
            }
            """);
        File.WriteAllText(Path.Combine(root, "ss-tests", "Tests.Codeunit.al"), """
            codeunit 64090 "SS Tests"
            {
                Subtype = Test;

                [Test]
                procedure TotalIs100()
                var
                    Mid: Codeunit "SS Mid";
                    Lib: Codeunit "SS Lib";
                begin
                    if Lib.Value() + Lib.Extra() <> 100 then
                        Error('SS total %1', Lib.Value() + Lib.Extra());
                    if Mid.Total() <> 43 then
                        Error('SS mid %1', Mid.Total());
                end;
            }
            """);
        WriteLib(extra: true);

        using var session = new WatchCycleSession($"\"{root}\"", cacheDir);
        try
        {
            var c1 = await session.Cycle(null);
            Assert.True(c1.Contains("PASS") && c1.Contains("TotalIs100"), "cycle 1 did not pass:\n" + c1);
            Assert.DoesNotContain("FAIL", c1);

            var c2 = await session.Cycle(() => WriteLib(extra: false));
            Assert.DoesNotContain("Function ID", c2);
            Assert.True(c2.Contains("AL0132"), "cycle 2 did not report AL0132 for the end of the chain:\n" + c2);
            // Both are rebuilt because of the library: Mid's own symbols did not change, but the
            // compile Tests' baseline came from saw Lib's old surface.
            foreach (var app in new[] { "SS Mid", "SS Tests" })
                Assert.True(c2.Split('\n').Any(l => l.Contains($"[watch] {app}: FULL REBUILD") && l.Contains("'SS Lib'")),
                    $"cycle 2 did not rebuild {app} because of the library:\n" + c2);
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
