// ServerMovedSubfolderAppTests — issue #5528.
//
// A --server request names a sourcePath BELOW the folder holding app.json (<root>/appA/test): the
// server registers that folder, and FindBucketRoot climbs to appA for the identity. When appA then
// moves and its version is bumped, the next request asked for <root>/new/test and aborted with a
// duplicate-app-id FATAL naming a folder that no longer exists. The registration must know its app
// root, so the move is seen; the #1683/#1892 reuse of a sibling that still holds the app must stay.
//
// Needs the BC artifact cache; skips when absent. A dedicated server: the sequence presents one AppId
// at two folders, which would poison the id cache for every other fact on a shared one.

using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerMovedSubfolderAppTests
{
    private const string AppId = "e8558855-5528-4a55-8855-285528555528";

    private static void WriteApp(string appDir, string version, int idBase, string codeunitName)
    {
        Directory.CreateDirectory(Path.Combine(appDir, "test"));
        // No "application": the Base Application floor is not the subject here.
        File.WriteAllText(Path.Combine(appDir, "app.json"), $$"""
        {
          "id": "{{AppId}}",
          "name": "Moved Subfolder 5528",
          "publisher": "Repro5528",
          "version": "{{version}}",
          "dependencies": [],
          "idRanges": [ { "from": {{idBase}}, "to": {{idBase + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(appDir, "test", "Probe.Codeunit.al"), $$"""
        codeunit {{idBase}} "{{codeunitName}}"
        {
            Subtype = Test;

            [Test]
            procedure APassingTest()
            begin
                if 1 <> 1 then
                    Error('unreachable');
            end;
        }
        """);
    }

    private static string Req(params string[] sourcePaths) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths,
        packagePaths = Array.Empty<string>(),
    });

    [SkippableFact]
    public async Task ASubfolderSourcePath_WhoseAppFolderMovesWithAVersionBump_IsRecompiledNotAFatal()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-server-moved-5528");
        var appA = Path.Combine(root, "appA");
        var appB = Path.Combine(root, "appB");
        var moved = Path.Combine(root, "moved");
        WriteApp(appA, "1.0.0.0", 62400, "MS5528 Probe A");
        WriteApp(appB, "1.0.0.0", 62410, "MS5528 Probe B");
        try
        {
            await using var server = await CliServer.StartAsync();

            // P1: appA/test and appB carry one identity and both folders still hold it (#1683/#1892),
            // so the second is the first's module. Must stay true with the app root recorded.
            var lines1 = await server.SendRequestStreamingAsync(
                Req(Path.Combine(appA, "test"), appB), TimeSpan.FromSeconds(180));
            var (_, d1) = ProtocolV2Streaming.Split(lines1);
            Assert.Equal(0, d1.GetProperty("exitCode").GetInt32());
            Assert.Equal(0, d1.GetProperty("failed").GetInt32());
            var err1 = await server.StdErrOfRequestAsync(1);
            Assert.Contains("V2_appB: AppId " + AppId, err1);
            Assert.Contains("already loaded earlier in this request/session", err1);
            Assert.DoesNotContain("no longer holds this app", err1);

            // P2: appA moves and its version is bumped; the request names the new folder's subfolder.
            Directory.Move(appA, moved);
            var manifest = Path.Combine(moved, "app.json");
            var text = File.ReadAllText(manifest);
            var edited = text.Replace("\"version\": \"1.0.0.0\"", "\"version\": \"2.0.0.0\"");
            Assert.NotEqual(text, edited); // guard: the substitution applied
            File.WriteAllText(manifest, edited);

            var lines2 = await server.SendRequestStreamingAsync(
                Req(Path.Combine(moved, "test")), TimeSpan.FromSeconds(180));
            Assert.DoesNotContain("duplicate app id", string.Join(" | ", lines2), StringComparison.OrdinalIgnoreCase);
            var (_, d2) = ProtocolV2Streaming.Split(lines2);
            Assert.Equal(0, d2.GetProperty("exitCode").GetInt32());
            Assert.Equal(1, d2.GetProperty("passed").GetInt32());
            Assert.Equal(0, d2.GetProperty("failed").GetInt32());

            // The moved folder is named, and nothing was reused from the module that has no source.
            var err2 = await server.StdErrOfRequestAsync(2);
            Assert.Contains(appA, err2);
            Assert.Contains("no longer holds this app", err2);
            Assert.DoesNotContain("already loaded earlier in this request/session", err2);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
