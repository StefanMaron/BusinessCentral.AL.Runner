// #5079: one --server process, two directories declaring the same app id. A later request for
// the second directory must answer for ITS source — what a fresh server answers — not run the
// module an earlier request compiled from the first. Byte-identical directories still share the
// module (#3250, ServerCrossBundleReuseRegistryReplayTests). Runner-specific (server module
// reuse), so it lives here, not in the al-language corpus.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

// #5110: facts that need no startup flag of their own share one --server (SharedCliServer).
public sealed class ServerModuleReuseSourceTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerModuleReuseSourceTests(SharedCliServer fixture) => _fixture = fixture;

    private static string Calc(int factor) => $$"""
        codeunit 65330 "Reuse Calc"
        {
            procedure Scale(Value: Integer): Integer
            begin
                exit(Value * {{factor}});
            end;
        }
        """;

    private static string CalcTests(int expected, string method = "Scale_Works") => $$"""
        codeunit 65331 "Reuse Calc Tests"
        {
            Subtype = Test;

            [Test]
            procedure {{method}}()
            var
                C: Codeunit "Reuse Calc";
            begin
                if C.Scale(21) <> {{expected}} then
                    Error('Scale returned %1', C.Scale(21));
            end;
        }
        """;

    // Calls a procedure no directory declares: a fresh server refuses to compile it (AL0132).
    private const string ExecMissing = """
        codeunit 65332 "Reuse Exec"
        {
            trigger OnRun()
            var
                C: Codeunit "Reuse Calc";
                F: Integer;
            begin
                F := C.QuadIt(5);
            end;
        }
        """;

    // Each fact declares its own id, shared only by that fact's two directories, so no fact on the
    // shared server can be answered from another fact's module (SharedCliServer rule (c)).
    private static string AppId(int fact, int app = 1) => $"c5079000-0000-4a11-9111-0000000000{fact}{app}";

    private static void WriteApp(string dir, string id, string name = "Reuse Source SX", string? dependsOn = null)
    {
        Directory.CreateDirectory(dir);
        var deps = dependsOn == null ? "[]" : $$"""
            [ { "id": "{{dependsOn}}", "name": "Reuse Source SX", "publisher": "AL Runner", "version": "1.0.0.0" } ]
            """;
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": {{deps}},
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 65330, "to": 65349 } ],
          "runtime": "14.0"
        }
        """);
    }

    private static async Task<(int Exit, Dictionary<string, string> Status, string Raw)> RunTests(CliServer server, params string[] bundles)
    {
        var stderrMark = server.StdErrMark;
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runTests", ["sourcePaths"] = bundles, ["packagePaths"] = Array.Empty<string>(),
        }), TimeSpan.FromSeconds(240));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        return (summary.GetProperty("exitCode").GetInt32(),
            events.ToDictionary(e => e.GetProperty("name").GetString()!.Split('.').Last(),
                e => e.GetProperty("status").GetString()!, StringComparer.Ordinal),
            string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErrSince(stderrMark));
    }

    /// <summary>The issue's reproducer: runTests on X, then execute on Y, whose source does not
    /// compile. A fresh server answers exit 3; before the fix this ran X's module and passed.</summary>
    [SkippableFact]
    public async Task RunTestsThenExecute_OtherDirectorySameId_CompilesItsOwnSource()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-reuse-5079-exec");
        var x = Path.Combine(root, "x");
        var y = Path.Combine(root, "y");
        WriteApp(x, AppId(1));
        File.WriteAllText(Path.Combine(x, "Calc.Codeunit.al"), Calc(2));
        File.WriteAllText(Path.Combine(x, "Tests.Codeunit.al"), CalcTests(42));
        WriteApp(y, AppId(1));
        File.WriteAllText(Path.Combine(y, "Calc.Codeunit.al"), Calc(2));
        File.WriteAllText(Path.Combine(y, "Exec.Codeunit.al"), ExecMissing);
        try
        {
            var server = await _fixture.GetAsync(new[] { "--no-cache" });
            var first = await RunTests(server, x);
            Assert.True(first.Exit == 0, first.Raw);

            var line = await server.SendAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["command"] = "execute", ["sourcePaths"] = new[] { y },
            }), TimeSpan.FromSeconds(240));
            using var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.GetProperty("exitCode").GetInt32() == 3, line + "\n" + server.StdErr);
            Assert.Contains("QuadIt", line, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>Same id, different source, both compile: each directory's test runs against its
    /// own code. Before the fix Y's test ran against X's Scale and failed.</summary>
    [SkippableFact]
    public async Task RunTestsTwice_OtherDirectoryDifferentSource_EachRunsItsOwnCode()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-reuse-5079-src");
        var x = Path.Combine(root, "x");
        var y = Path.Combine(root, "y");
        WriteApp(x, AppId(2));
        File.WriteAllText(Path.Combine(x, "Calc.Codeunit.al"), Calc(2));
        File.WriteAllText(Path.Combine(x, "Tests.Codeunit.al"), CalcTests(42));
        WriteApp(y, AppId(2));
        File.WriteAllText(Path.Combine(y, "Calc.Codeunit.al"), Calc(3));
        File.WriteAllText(Path.Combine(y, "Tests.Codeunit.al"), CalcTests(63, "Scale_TriplesInY"));
        try
        {
            var server = await _fixture.GetAsync(new[] { "--no-cache" });
            var first = await RunTests(server, x);
            Assert.True(first.Exit == 0 && first.Status["Scale_Works"] == "pass", first.Raw);
            // Y's own test, and only it: X's module would run X's test instead.
            var second = await RunTests(server, y);
            Assert.True(second.Exit == 0, second.Raw);
            Assert.Equal(new[] { "Scale_TriplesInY" }, second.Status.Keys.ToArray());
            Assert.True(second.Status["Scale_TriplesInY"] == "pass", second.Raw);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>The dependency route to the same reuse: X alone first, then a test app in another
    /// root whose dependency is Y's app, found as a sibling source folder. That dependency must be
    /// compiled from Y, not handed X's module because the id matches.</summary>
    [SkippableFact]
    public async Task RunTestsThenSiblingDependency_OtherDirectorySameId_IsItsOwnSource()
    {
        TestArtifacts.SkipIfMissing();
        var rootX = TestScratch.Dir("al-runner-server-reuse-5079-dep-x");
        var rootY = TestScratch.Dir("al-runner-server-reuse-5079-dep-y");
        var x = Path.Combine(rootX, "x");
        var yApp = Path.Combine(rootY, "y-app");
        var yTest = Path.Combine(rootY, "y-test");
        WriteApp(x, AppId(3));
        File.WriteAllText(Path.Combine(x, "Calc.Codeunit.al"), Calc(2));
        File.WriteAllText(Path.Combine(x, "Tests.Codeunit.al"), CalcTests(42));
        WriteApp(yApp, AppId(3));
        File.WriteAllText(Path.Combine(yApp, "Calc.Codeunit.al"), Calc(3));
        WriteApp(yTest, AppId(3, app: 2), name: "Reuse Source Tests SX", dependsOn: AppId(3));
        File.WriteAllText(Path.Combine(yTest, "Tests.Codeunit.al"), CalcTests(63, "Scale_TriplesInY"));
        try
        {
            var server = await _fixture.GetAsync(new[] { "--no-cache" });
            var first = await RunTests(server, x);
            Assert.True(first.Exit == 0, first.Raw);
            var second = await RunTests(server, yTest);
            Assert.True(second.Exit == 0 && second.Status["Scale_TriplesInY"] == "pass", second.Raw);
        }
        finally
        {
            try { Directory.Delete(rootX, recursive: true); } catch { }
            try { Directory.Delete(rootY, recursive: true); } catch { }
        }
    }
}
