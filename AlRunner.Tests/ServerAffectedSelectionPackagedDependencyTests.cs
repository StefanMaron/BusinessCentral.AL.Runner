// ServerAffectedSelectionPackagedDependencyTests — #4973: the app under test is a packaged
// dependency (App.Test/.alpackages/App.app) with its source folder App/ next to the test app, and
// the request names only the test app. Statements the package executes are attributed to App/'s
// files, which no request module tracks. Mechanism: docs/server-mode.md#affectedonly-and-packaged-dependencies.
using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public class ServerAffectedSelectionPackagedDependencyTests
{
    private static readonly Guid AppId = Guid.Parse("1cbc1e9b-115c-4afd-b266-0a86b8ed313c");
    private static readonly Guid TestAppId = Guid.Parse("bf420b6a-4c85-494d-9429-0802dd697ef2");

    // Twice(Integer): Integer, because its BC method id is the one ServerTableRelationReloadTests
    // already declares by hand in a package's SymbolReference.json.
    private static string HelperSource(int factor) => $$"""
        codeunit 60471 "PkgDep Helper SX"
        {
            procedure Twice(Value: Integer): Integer
            begin
                exit(Value * {{factor}});
            end;
        }
        """;

    private const string TestsSource = """
        codeunit 60480 "PkgDep Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure CallsApp()
            var
                H: Codeunit "PkgDep Helper SX";
            begin
                if H.Twice(21) <> 42 then
                    Error('CallsApp: the app returned %1', H.Twice(21));
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
                ? new[] { new { id = AppId, name = "PkgDep App", publisher = "AL Runner", version = "1.0.0.0" } }
                : Array.Empty<object>(),
        }));
    }

    /// <summary>App/ (source), App.Test/ (source) and App.Test/.alpackages/App.app built from App/.</summary>
    private static (string App, string TestApp) Layout()
    {
        var root = TestScratch.Dir("al-runner-server-affected-pkgdep");
        var app = Path.Combine(root, "App");
        var testApp = Path.Combine(root, "App.Test");
        WriteManifest(app, AppId, "PkgDep App", dependsOnApp: false);
        Directory.CreateDirectory(Path.Combine(app, "src"));
        File.WriteAllText(Path.Combine(app, "src", "Helper.Codeunit.al"), HelperSource(2));
        WriteManifest(testApp, TestAppId, "PkgDep App Test", dependsOnApp: true);
        File.WriteAllText(Path.Combine(testApp, "Tests.Codeunit.al"), TestsSource);
        Package(app, testApp);
        return (app, testApp);
    }

    // Rebuilds App.app in place from App/, same identity and version — what a developer's build does.
    private static void Package(string app, string testApp)
    {
        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(app, "app.json"))!;
        var packages = Path.Combine(testApp, ".alpackages");
        Directory.CreateDirectory(packages);
        var symbols = JsonSerializer.SerializeToUtf8Bytes(new
        {
            AppId, Name = "PkgDep App", Publisher = "AL Runner", Version = "1.0.0.0", RuntimeVersion = "14.0",
            Codeunits = new[] { new { Id = 60471, Name = "PkgDep Helper SX", Methods = new[] { new {
                Id = 1516892452, Name = "Twice", ReturnTypeDefinition = new { Name = "Integer" },
                Parameters = new[] { new { Name = "Value", TypeDefinition = new { Name = "Integer" } } }
            } } } },
        });
        InProcessAppPackager.EmitAppPackageToFile(app, identity,
            Path.Combine(packages, "AL Runner_PkgDep App_1.0.0.0.app"), symbols);
    }

    private static string Request(string testApp) => JsonSerializer.Serialize(new
    {
        command = "runTests",
        sourcePaths = new[] { testApp },
        packagePaths = Array.Empty<string>(),
        affectedOnly = true,
    });

    private sealed record Observed(Dictionary<string, string> Status, int Ran, int Skipped, bool ForcedFull, string Raw);

    private static async Task<Observed> Send(CliServer server, string testApp)
    {
        var lines = await server.SendRequestStreamingAsync(Request(testApp), TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErr;
        Assert.True(summary.TryGetProperty("selection", out var selection), raw);
        return new Observed(
            events.ToDictionary(e => e.GetProperty("name").GetString()!.Split('.').Last(),
                e => e.GetProperty("status").GetString()!, StringComparer.Ordinal),
            selection.GetProperty("ran").GetInt32(), selection.GetProperty("skipped").GetInt32(),
            selection.GetProperty("forcedFull").GetBoolean(), raw);
    }

    [SkippableFact]
    public async Task UnchangedRequest_SkipsTheTestThatCallsIntoThePackagedApp()
    {
        TestArtifacts.SkipIfMissing();
        var (_, testApp) = Layout();
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, testApp);
        Assert.True(baseline.Status.GetValueOrDefault("CallsApp") == "pass", baseline.Raw);
        Assert.True(baseline.Status.GetValueOrDefault("StaysInTestApp") == "pass", baseline.Raw);

        var unchanged = await Send(server, testApp);
        Assert.False(unchanged.ForcedFull, unchanged.Raw);
        Assert.Equal(0, unchanged.Ran);
        Assert.Equal(2, unchanged.Skipped);
    }

    // The same, served from the AL-output cache: a second server on the cache the first one filled.
    [SkippableFact]
    public async Task UnchangedRequest_OnAWarmCache_SkipsTheTestThatCallsIntoThePackagedApp()
    {
        TestArtifacts.SkipIfMissing();
        var (_, testApp) = Layout();
        var cache = TestScratch.Dir("al-runner-server-affected-pkgdep-cache");
        for (var server = 1; server <= 2; server++)
        {
            await using var s = await CliServer.StartAsync(new[] { "--cache", cache });
            var first = await Send(s, testApp);
            Assert.True(first.Status.GetValueOrDefault("CallsApp") == "pass", first.Raw);
            var unchanged = await Send(s, testApp);
            Assert.False(unchanged.ForcedFull, $"server {server}: {unchanged.Raw}");
            Assert.Equal(0, unchanged.Ran);
            Assert.Equal(2, unchanged.Skipped);
        }
    }

    [SkippableFact]
    public async Task RebuiltPackage_SameVersion_RunsTheTestThatCallsIntoIt_AgainstTheNewCode()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp) = Layout();
        await using var server = await CliServer.StartAsync(new[] { "--no-cache" });

        var baseline = await Send(server, testApp);
        Assert.True(baseline.Status.GetValueOrDefault("CallsApp") == "pass", baseline.Raw);
        var unchanged = await Send(server, testApp);
        Assert.True(unchanged.Ran == 0, unchanged.Raw);

        File.WriteAllText(Path.Combine(app, "src", "Helper.Codeunit.al"), HelperSource(3));
        Package(app, testApp);

        // Same AppId and version, so only the package's content can tell the two apart.
        var afterRebuild = await Send(server, testApp);
        Assert.True(afterRebuild.ForcedFull, afterRebuild.Raw);
        Assert.Contains("environment changed", afterRebuild.Raw, StringComparison.Ordinal);
        Assert.True(afterRebuild.Status.TryGetValue("CallsApp", out var status), afterRebuild.Raw);
        Assert.True(status == "fail", afterRebuild.Raw);
        Assert.Contains("the app returned 63", afterRebuild.Raw, StringComparison.Ordinal);
    }
}
