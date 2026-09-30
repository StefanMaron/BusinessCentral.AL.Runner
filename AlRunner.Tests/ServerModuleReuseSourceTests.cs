// #5079: one --server process, two directories declaring the same app id. A later request for
// the second directory must answer for ITS source — what a fresh server answers — not run the
// module an earlier request compiled from the first. Byte-identical directories still share the
// module (#3250, ServerCrossBundleReuseRegistryReplayTests). Runner-specific (server module
// reuse), so it lives here, not in the al-language corpus.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class ServerModuleReuseSourceTests
{
    private static string Calc(int factor) => $$"""
        codeunit 65330 "Reuse Calc"
        {
            procedure Scale(Value: Integer): Integer
            begin
                exit(Value * {{factor}});
            end;
        }
        """;

    private static string CalcTests(int expected) => $$"""
        codeunit 65331 "Reuse Calc Tests"
        {
            Subtype = Test;

            [Test]
            procedure Scale_Works()
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

    private const string AppId = "c5079000-0000-4a11-9111-000000000001";

    private static void WriteApp(string dir, string id = AppId, string name = "Reuse Source SX", string? dependsOn = null)
    {
        Directory.CreateDirectory(dir);
        var deps = dependsOn == null ? "[]" : $$"""
            [ { "id": "{{AppId}}", "name": "Reuse Source SX", "publisher": "AL Runner", "version": "1.0.0.0" } ]
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
        var lines = await server.SendRequestStreamingAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "runTests", ["sourcePaths"] = bundles, ["packagePaths"] = Array.Empty<string>(),
        }), TimeSpan.FromSeconds(240));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        return (summary.GetProperty("exitCode").GetInt32(),
            events.ToDictionary(e => e.GetProperty("name").GetString()!.Split('.').Last(),
                e => e.GetProperty("status").GetString()!, StringComparer.Ordinal),
            string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr);
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
        WriteApp(x);
        File.WriteAllText(Path.Combine(x, "Calc.Codeunit.al"), Calc(2));
        File.WriteAllText(Path.Combine(x, "Tests.Codeunit.al"), CalcTests(42));
        WriteApp(y);
        File.WriteAllText(Path.Combine(y, "Calc.Codeunit.al"), Calc(2));
        File.WriteAllText(Path.Combine(y, "Exec.Codeunit.al"), ExecMissing);
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
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
        WriteApp(x);
        File.WriteAllText(Path.Combine(x, "Calc.Codeunit.al"), Calc(2));
        File.WriteAllText(Path.Combine(x, "Tests.Codeunit.al"), CalcTests(42));
        WriteApp(y);
        File.WriteAllText(Path.Combine(y, "Calc.Codeunit.al"), Calc(3));
        File.WriteAllText(Path.Combine(y, "Tests.Codeunit.al"), CalcTests(63));
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
            var first = await RunTests(server, x);
            Assert.True(first.Exit == 0 && first.Status["Scale_Works"] == "pass", first.Raw);
            var second = await RunTests(server, y);
            Assert.True(second.Exit == 0 && second.Status["Scale_Works"] == "pass", second.Raw);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>The dependency route to the same reuse: X alone first, then Y's app with a test
    /// app depending on it. The test app loads Y's app as a dependency, which must be compiled
    /// from Y, not handed X's module because the id matches.</summary>
    [SkippableFact]
    public async Task RunTestsThenTwoBundles_DependencyFromOtherDirectory_IsItsOwnSource()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-server-reuse-5079-dep");
        var x = Path.Combine(root, "x");
        var yApp = Path.Combine(root, "y-app");
        var yTest = Path.Combine(root, "y-test");
        WriteApp(x);
        File.WriteAllText(Path.Combine(x, "Calc.Codeunit.al"), Calc(2));
        File.WriteAllText(Path.Combine(x, "Tests.Codeunit.al"), CalcTests(42));
        WriteApp(yApp);
        File.WriteAllText(Path.Combine(yApp, "Calc.Codeunit.al"), Calc(3));
        WriteApp(yTest, id: "c5079000-0000-4a11-9111-000000000002", name: "Reuse Source Tests SX", dependsOn: AppId);
        File.WriteAllText(Path.Combine(yTest, "Tests.Codeunit.al"), CalcTests(63));
        try
        {
            await using var server = await CliServer.StartAsync(new[] { "--no-cache" });
            var first = await RunTests(server, x);
            Assert.True(first.Exit == 0, first.Raw);
            var second = await RunTests(server, yApp, yTest);
            Assert.True(second.Exit == 0 && second.Status["Scale_Works"] == "pass", second.Raw);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
