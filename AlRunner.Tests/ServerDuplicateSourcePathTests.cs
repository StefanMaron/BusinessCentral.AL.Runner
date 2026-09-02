using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2136 over <c>--server</c> instead of the CLI. The reported symptom was the
/// CLI's (<c>al-runner ./x ./x</c> ran the bundle twice and reported twice the tests),
/// but <c>RunAllBundlesForServer</c> made the same assumption one call site over: it
/// iterated <c>sourcePaths</c> as given, so a request naming the same directory twice
/// produced two <c>ServerRunResult</c>s, which <c>HandleRunTests</c> then
/// <c>SelectMany</c>'d into a doubled test list and a doubled summary <c>total</c>.
///
/// RED (pre-fix): <c>total</c> is 2 for a 1-test bundle named twice.
/// GREEN (post-fix): 1 — the duplicate is dropped by resolved real path, with a notice
/// on stderr naming the argument that went.
///
/// The negative direction matters just as much and is pinned here too: two GENUINELY
/// DIFFERENT directories must still both run. Identity is deliberately not the dedup
/// key (see <see cref="AlRunner.Infrastructure.BundleRootDeduplication"/>).
///
/// Spawns the real runner in --server mode; needs the BC artifact cache. Skips when absent.
/// </summary>
public class ServerDuplicateSourcePathTests
{
    private static string MakeBundle(string dirName, string appId, int idFrom, int codeunitId)
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-server-dup-2136", Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, dirName);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{appId}}",
          "name": "Dup2136 {{dirName}}",
          "publisher": "Repro2136",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 4}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Dup2136Tests.al"), $$"""
        codeunit {{codeunitId}} "Dup2136 Tests {{codeunitId}}"
        {
            Subtype = Test;

            [Test]
            procedure TheOnlyTest()
            var
                Total: Integer;
            begin
                Total := 20 + 22;
                if Total <> 42 then
                    Error('expected 42, got %1', Total);
            end;
        }
        """);
        return dir;
    }

    private static JsonElement Summary(List<string> lines)
    {
        var (_, d) = ProtocolV2Streaming.Split(lines);
        return d;
    }

    [SkippableFact]
    public async Task RunTests_SameSourcePathTwice_RunsOnceAndReportsOneTest()
    {
        TestArtifacts.SkipIfMissing();

        var dir = MakeBundle("only", "d4e5f6a7-2136-4a1b-9c3d-000000000001", 62410, 62410);
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-dup-2136-cache", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        var req = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { dir, dir },
            packagePaths = Array.Empty<string>(),
        });
        var lines = await server.SendRequestStreamingAsync(req, TimeSpan.FromSeconds(180));
        var d = Summary(lines);

        // The concrete numbers ARE the claim — a 1-test bundle named twice is 1 test,
        // not 2. Asserting "it ran" would pass against the broken behaviour.
        Assert.Equal(1, d.GetProperty("total").GetInt32());
        Assert.Equal(1, d.GetProperty("passed").GetInt32());
        Assert.Equal(0, d.GetProperty("failed").GetInt32());
        Assert.Equal(0, d.GetProperty("errors").GetInt32());
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());
        Assert.True(server.StdErr.Contains("duplicate bundle argument"), server.StdErr);
    }

    [SkippableFact]
    public async Task RunTests_TwoDistinctSourcePaths_StillRunBoth()
    {
        TestArtifacts.SkipIfMissing();

        var a = MakeBundle("first", "d4e5f6a7-2136-4a1b-9c3d-000000000002", 62420, 62420);
        var b = MakeBundle("second", "d4e5f6a7-2136-4a1b-9c3d-000000000003", 62425, 62425);
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-server-dup-2136-cache", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        var req = JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { a, b },
            packagePaths = Array.Empty<string>(),
        });
        var lines = await server.SendRequestStreamingAsync(req, TimeSpan.FromSeconds(180));
        var d = Summary(lines);

        Assert.Equal(2, d.GetProperty("total").GetInt32());
        Assert.Equal(2, d.GetProperty("passed").GetInt32());
        Assert.Equal(0, d.GetProperty("exitCode").GetInt32());
        Assert.True(!server.StdErr.Contains("duplicate bundle argument"), server.StdErr);
    }
}
