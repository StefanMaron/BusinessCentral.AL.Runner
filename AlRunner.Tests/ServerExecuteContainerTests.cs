// #5107: `execute` on a source path that only contains apps serves one bundle per app, each
// compiled under its own app.json — so it returns one result per app, and a container whose apps
// break the dependency-visibility rule is refused with the CLI's AL0185 instead of being merged
// into one module that compiles. docs/server-mode.md#execute states the contract.
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public class ServerExecuteContainerTests
{
    private static readonly string CoverageDependencySource = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "CoverageDependencySource"));

    private static async Task<(JsonElement Response, string Raw)> ExecuteAsync(string scratch, string sourcePath)
    {
        var args = new List<string> { "--cache", Path.Combine(scratch, "al-out") };
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) { args.Add("--package-cache"); args.Add(platformApps); }
        await using var server = await CliServer.StartAsync(args);
        var raw = await server.SendAsync(JsonSerializer.Serialize(new
        {
            command = "execute",
            sourcePaths = new[] { sourcePath },
            packagePaths = Array.Empty<string>(),
        }), TimeSpan.FromSeconds(300));
        return (JsonSerializer.Deserialize<JsonElement>(raw), raw + "\n--- server stderr ---\n" + server.StdErr);
    }

    /// <summary>The fixture's three apps (dep, main, run) each carry an OnRun codeunit: one result per app.</summary>
    [SkippableFact]
    public async Task Execute_ContainerOfApps_ReturnsOneResultPerApp()
    {
        TestArtifacts.SkipIfMissing();
        var (response, raw) = await ExecuteAsync(TestScratch.Dir("exec-container-per-app"), CoverageDependencySource);

        Assert.False(response.TryGetProperty("error", out _), raw);
        var names = response.GetProperty("tests").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.True(names.SequenceEqual(new[] { "Codeunit70860.OnRun", "Codeunit70870.OnRun", "Codeunit70880.OnRun" }), raw);
    }

    /// <summary>
    /// run-app depends on middle-app only; middle-app depends on base-app without propagating it.
    /// run-app's OnRun names base-app's codeunit, which the CLI refuses with AL0185.
    /// </summary>
    [SkippableFact]
    public async Task Execute_ContainerWithUndeclaredTransitiveDependency_IsRefusedWithAL0185()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("exec-container-al0185");
        var root = Path.Combine(scratch, "ws");
        Guid baseId = Guid.NewGuid(), middleId = Guid.NewGuid(), runId = Guid.NewGuid();
        void App(string dir, Guid id, string name, string deps, int from, string file, string al)
        {
            var path = Path.Combine(root, dir);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "app.json"), $$"""
            { "id": "{{id}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0",
              "dependencies": [ {{deps}} ], "propagateDependencies": false, "platform": "1.0.0.0",
              "idRanges": [ { "from": {{from}}, "to": {{from + 4}} } ], "runtime": "14.0" }
            """);
            File.WriteAllText(Path.Combine(path, file), al);
        }
        string Dep(Guid id, string name) =>
            $$"""{ "id": "{{id}}", "name": "{{name}}", "publisher": "AL Runner", "version": "1.0.0.0" }""";

        App("base-app", baseId, "SEC Base", "", 60090, "Base.Codeunit.al", """
            codeunit 60090 "SEC Base Api"
            {
                procedure Value(): Integer
                begin
                    exit(7);
                end;
            }
            """);
        App("middle-app", middleId, "SEC Middle", Dep(baseId, "SEC Base"), 60095, "Middle.Codeunit.al", """
            codeunit 60095 "SEC Middle Api"
            {
                procedure Value(): Integer
                var
                    BaseApi: Codeunit "SEC Base Api";
                begin
                    exit(BaseApi.Value());
                end;
            }
            """);
        App("run-app", runId, "SEC Run", Dep(middleId, "SEC Middle"), 60100, "Run.Codeunit.al", """
            codeunit 60100 "SEC Run"
            {
                trigger OnRun()
                var
                    BaseApi: Codeunit "SEC Base Api";
                begin
                    if BaseApi.Value() <> 7 then
                        Error('unexpected');
                end;
            }
            """);

        var (response, raw) = await ExecuteAsync(scratch, root);

        var errors = response.TryGetProperty("compilationErrors", out var groups)
            ? groups.EnumerateArray().SelectMany(g => g.GetProperty("errors").EnumerateArray().Select(e => e.GetString() ?? "")).ToList()
            : new List<string>();
        Assert.True(errors.Any(e => e.Contains("AL0185: Codeunit 'SEC Base Api' is missing")), raw);
        Assert.True(response.GetProperty("exitCode").GetInt32() != 0, raw);
        if (response.TryGetProperty("tests", out var tests))
            Assert.False(tests.EnumerateArray().Any(t => t.GetProperty("name").GetString() == "Codeunit60100.OnRun"
                && t.GetProperty("status").GetString() == "pass"), raw);
    }
}
