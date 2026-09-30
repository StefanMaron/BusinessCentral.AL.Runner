// ServerAffectedSelectionEnvironmentDriftTests — #5028: a persisted affectedOnly baseline recorded in
// another environment is used, narrowed by a per-object diff of the two environments' apps, with a
// warning; never a full run for that reason alone, unless the request sets strictEnvironment.
// Two environments are simulated by two builds of one dependency package (App.Test/.alpackages/App.app)
// that differ in one object, as a new BC build differs in some Base Application objects.
// Mechanism: docs/server-mode.md#affectedonly-across-environments.
using System.Text.Json;
using System.Text.Json.Nodes;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionEnvironmentDriftTests
{
    private static readonly Guid AppId = Guid.Parse("5d0c7a2e-4f35-4b8e-9a4e-3c1f6b7e2d11");
    private static readonly Guid TestAppId = Guid.Parse("a8e3b1c4-0d2f-4f6a-8b9c-1e2d3f4a5b6c");

    // Twice(Integer): Integer on both codeunits: its BC method id is the one
    // ServerAffectedSelectionPackagedDependencyTests declares in the package's symbols.
    private static string HelperSource(int factor) => $$"""
        codeunit 60471 "Drift Helper SX"
        {
            procedure Twice(Value: Integer): Integer
            begin
                exit(Value * {{factor}});
            end;
        }
        """;

    private const string OtherSource = """
        codeunit 60472 "Drift Other SX"
        {
            procedure Twice(Value: Integer): Integer
            begin
                exit(Value * 2);
            end;
        }
        """;

    private const string TestsSource = """
        codeunit 60480 "Drift Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure CallsHelper()
            var
                H: Codeunit "Drift Helper SX";
            begin
                if H.Twice(21) <> 42 then
                    Error('CallsHelper: the app returned %1', H.Twice(21));
            end;

            [Test]
            procedure CallsOther()
            var
                O: Codeunit "Drift Other SX";
            begin
                if O.Twice(21) <> 42 then
                    Error('CallsOther: the app returned %1', O.Twice(21));
            end;

            [Test]
            procedure StaysInTestApp()
            var
                X: Integer;
            begin
                X := 2;
                if X <> 2 then
                    Error('StaysInTestApp failed');
            end;
        }
        """;

    private static void WriteManifest(string dir, Guid id, string name, bool dependsOnApp)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), JsonSerializer.Serialize(new
        {
            id,
            name,
            publisher = "AL Runner",
            version = "1.0.0.0",
            platform = "1.0.0.0",
            runtime = "14.0",
            idRanges = new[] { new { from = 60470, to = 60489 } },
            dependencies = dependsOnApp
                ? new[] { new { id = AppId, name = "Drift App", publisher = "AL Runner", version = "1.0.0.0" } }
                : Array.Empty<object>(),
        }));
    }

    private static (string App, string TestApp, string Cache) Layout(string name)
    {
        var root = TestScratch.Dir("al-runner-server-affected-drift-" + name);
        var app = Path.Combine(root, "App");
        var testApp = Path.Combine(root, "App.Test");
        WriteManifest(app, AppId, "Drift App", dependsOnApp: false);
        Directory.CreateDirectory(Path.Combine(app, "src"));
        File.WriteAllText(Path.Combine(app, "src", "Helper.Codeunit.al"), HelperSource(2));
        File.WriteAllText(Path.Combine(app, "src", "Other.Codeunit.al"), OtherSource);
        WriteManifest(testApp, TestAppId, "Drift App Test", dependsOnApp: true);
        File.WriteAllText(Path.Combine(testApp, "Tests.Codeunit.al"), TestsSource);
        Package(app, testApp);
        return (app, testApp, Path.Combine(root, "cache"));
    }

    // Rebuilds App.app in place from App/, same identity and version: the second environment.
    private static void Package(string app, string testApp)
    {
        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(app, "app.json"))!;
        var packages = Path.Combine(testApp, ".alpackages");
        Directory.CreateDirectory(packages);
        object Codeunit(int id, string name) => new
        {
            Id = id, Name = name, Methods = new[] { new {
                Id = 1516892452, Name = "Twice", ReturnTypeDefinition = new { Name = "Integer" },
                Parameters = new[] { new { Name = "Value", TypeDefinition = new { Name = "Integer" } } }
            } },
        };
        var symbols = JsonSerializer.SerializeToUtf8Bytes(new
        {
            AppId, Name = "Drift App", Publisher = "AL Runner", Version = "1.0.0.0", RuntimeVersion = "14.0",
            Codeunits = new[] { Codeunit(60471, "Drift Helper SX"), Codeunit(60472, "Drift Other SX") },
        });
        InProcessAppPackager.EmitAppPackageToFile(app, identity,
            Path.Combine(packages, "AL Runner_Drift App_1.0.0.0.app"), symbols);
    }

    private static void ChangeHelper(string app, string testApp)
    {
        File.WriteAllText(Path.Combine(app, "src", "Helper.Codeunit.al"), HelperSource(3));
        Package(app, testApp);
    }

    // Test isolation: each test's own recording decides, with no widening to its codeunit (#5035).
    private static string Request(string testApp, bool strict = false) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { testApp },
        packagePaths = Array.Empty<string>(),
        affectedOnly = true,
        testIsolation = "test",
        strictEnvironment = strict,
    });

    private sealed record Observed(Dictionary<string, string> Status, int Ran, int Skipped, bool ForcedFull,
        string? Reason, JsonElement? Drift, string Raw);

    private static async Task<Observed> Send(CliServer server, string testApp, bool strict = false)
    {
        var lines = await server.SendRequestStreamingAsync(Request(testApp, strict), TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr;
        Assert.True(summary.TryGetProperty("selection", out var selection), raw);
        return new Observed(
            events.ToDictionary(e => e.GetProperty("name").GetString()!.Split('.').Last(),
                e => e.GetProperty("status").GetString()!, StringComparer.Ordinal),
            selection.GetProperty("ran").GetInt32(), selection.GetProperty("skipped").GetInt32(),
            selection.GetProperty("forcedFull").GetBoolean(),
            selection.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
            selection.TryGetProperty("environmentDrift", out var drift) ? drift.Clone() : null, raw);
    }

    private static async Task RecordBaseline(string testApp, string cache)
    {
        await using var first = await CliServer.StartAsync(new[] { "--cache", cache });
        var baseline = await Send(first, testApp);
        Assert.True(baseline.Status.Count == 3 && baseline.Status.Values.All(s => s == "pass"), baseline.Raw);
    }

    [SkippableFact]
    public async Task OneObjectDiffers_SelectsTheTestThatReachedIt_AndWarnsNamingBothBuildsAndTheObject()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp, cache) = Layout("diffed");
        await RecordBaseline(testApp, cache);
        ChangeHelper(app, testApp);

        await using (var second = await CliServer.StartAsync(new[] { "--cache", cache }))
        {
            var drifted = await Send(second, testApp);
            Assert.False(drifted.ForcedFull, drifted.Raw);
            Assert.Equal(new[] { "CallsHelper" }, drifted.Status.Keys);
            Assert.True(drifted.Status["CallsHelper"] == "fail", drifted.Raw);
            Assert.Contains("the app returned 63", drifted.Raw, StringComparison.Ordinal);
            Assert.Equal(1, drifted.Ran);
            Assert.Equal(2, drifted.Skipped);

            var d = drifted.Drift ?? throw new Xunit.Sdk.XunitException("no environmentDrift: " + drifted.Raw);
            Assert.Equal("diffed", d.GetProperty("mode").GetString());
            Assert.Equal(1, d.GetProperty("changedObjects").GetInt32());
            Assert.Equal(new[] { "Codeunit 60471 Drift Helper SX" },
                d.GetProperty("objects").EnumerateArray().Select(o => o.GetString()));
            var recorded = d.GetProperty("recorded").GetString();
            var current = d.GetProperty("current").GetString();
            Assert.False(string.IsNullOrEmpty(recorded), drifted.Raw);
            Assert.False(string.IsNullOrEmpty(current), drifted.Raw);
            Assert.Contains($"WARNING: the affectedOnly baseline was recorded in another environment (BC {recorded}, now BC {current}); "
                + "1 object(s) differ: Codeunit 60471 Drift Helper SX", drifted.Raw, StringComparison.Ordinal);

            // Recorded again in this environment, so the next request narrows with no warning.
            var settled = await Send(second, testApp);
            Assert.Null(settled.Drift);
            Assert.Equal(0, settled.Ran);
        }

        // A third process on the same cache root reads the baseline the second one recorded.
        await using var third = await CliServer.StartAsync(new[] { "--cache", cache });
        var reloaded = await Send(third, testApp);
        Assert.False(reloaded.ForcedFull, reloaded.Raw);
        Assert.Null(reloaded.Drift);
        Assert.Equal(0, reloaded.Ran);
    }

    [SkippableFact]
    public async Task NoPerObjectRecord_UsesTheBaselineAsIs_AndSaysItIsApproximate()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp, cache) = Layout("approximate");
        await RecordBaseline(testApp, cache);

        // A baseline written before #5028 carries no record of its environment's objects.
        var file = Assert.Single(Directory.GetFiles(Path.Combine(cache, AffectedBaselineStore.CacheName), "*.json"));
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["Schema"] = 4;
        json.Remove("EnvApps");
        foreach (var bundle in json["Bundles"]!.AsObject()) bundle.Value!.AsObject().Remove("EnvApps");
        File.WriteAllText(file, json.ToJsonString());
        ChangeHelper(app, testApp);

        await using var second = await CliServer.StartAsync(new[] { "--cache", cache });
        var drifted = await Send(second, testApp);
        Assert.False(drifted.ForcedFull, drifted.Raw);
        Assert.Equal(0, drifted.Ran);
        Assert.Equal(3, drifted.Skipped);
        var d = drifted.Drift ?? throw new Xunit.Sdk.XunitException("no environmentDrift: " + drifted.Raw);
        Assert.Equal("approximate", d.GetProperty("mode").GetString());
        Assert.Contains("no per-object record", d.GetProperty("reason").GetString());
        Assert.Contains("Selection is APPROXIMATE", drifted.Raw, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task StrictEnvironment_RunsEverything_AsBefore()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp, cache) = Layout("strict");
        await RecordBaseline(testApp, cache);
        ChangeHelper(app, testApp);

        await using var second = await CliServer.StartAsync(new[] { "--cache", cache });
        var strict = await Send(second, testApp, strict: true);
        Assert.True(strict.ForcedFull, strict.Raw);
        Assert.Contains("environment changed", strict.Reason, StringComparison.Ordinal);
        Assert.Contains("strictEnvironment", strict.Reason, StringComparison.Ordinal);
        Assert.Null(strict.Drift);
        Assert.Equal(3, strict.Ran);
        Assert.True(strict.Status["CallsHelper"] == "fail", strict.Raw);
    }
}
