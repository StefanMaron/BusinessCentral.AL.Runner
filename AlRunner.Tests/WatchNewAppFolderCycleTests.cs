// WatchNewAppFolderCycleTests — issue #5526 end to end: a second app folder that appears under the watched
// root starts a cycle, and that cycle runs the new app. The folder arrives by Directory.Move from a staging
// folder, so the only event the watcher gets is the one for the folder itself.
using Xunit;

namespace AlRunner.Tests;

public class WatchNewAppFolderCycleTests
{
    private static void WriteApp(string folder, string id, string name, int from, string testName)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "app.json"), $$"""
        { "id": "{{id}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0", "dependencies": [],
          "idRanges": [ { "from": {{from}}, "to": {{from + 4}} } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(folder, "T.Codeunit.al"),
            $"codeunit {from} \"{name} Tests\"\n{{\n    Subtype = Test;\n    [Test]\n    procedure {testName}()\n    begin\n    end;\n}}\n");
    }

    [SkippableFact]
    public async Task Watch_AnAppFolderThatAppearsAfterTheWatchArmed_RunsACycleWithTheNewApp()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-watch-newapp");
        var stage = Path.Combine(TestScratch.Dir("al-runner-watch-newapp-stage"), "solo2");
        var cacheDir = TestScratch.Dir("al-runner-watch-newapp-cache");
        WriteApp(Path.Combine(root, "solo"), "52600000-0000-4000-8000-0000000fc001", "NF Solo", 64340, "Solo_Runs");
        WriteApp(stage, "52600000-0000-4000-8000-0000000fc002", "NF Solo Two", 64350, "Second_Runs");

        using var session = new WatchCycleSession($"\"{root}\"", cacheDir);
        try
        {
            var c1 = await session.Cycle(null);
            Assert.True(c1.Contains("Solo_Runs") && !c1.Contains("Second_Runs") && !c1.Contains("FAIL"),
                "cycle 1 should run only the first app:\n" + c1);

            var c2 = await session.Cycle(() => Directory.Move(stage, Path.Combine(root, "solo2")));
            Assert.True(c2.Contains("Second_Runs") && !c2.Contains("FAIL"),
                "cycle 2 (an app folder appeared) did not run the new app:\n" + c2);
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }
}
