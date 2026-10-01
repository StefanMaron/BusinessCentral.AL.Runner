// ServerAffectedSelectionPackagedDependencyTests — #4973: the app under test is a packaged
// dependency (App.Test/.alpackages/App.app) with its source folder App/ next to the test app, and
// the request names only the test app. Statements the package executes are attributed to App/'s
// files, which no request module tracks. Mechanism: docs/server-mode.md#affectedonly-and-packaged-dependencies.
using System.Text.Json;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

// #5110: facts that need no startup flag of their own share one --server (SharedCliServer).
public class ServerAffectedSelectionPackagedDependencyTests : IClassFixture<SharedCliServer>
{
    private readonly SharedCliServer _fixture;

    public ServerAffectedSelectionPackagedDependencyTests(SharedCliServer fixture) => _fixture = fixture;

    // Fresh ids per Layout(): facts share one server, so no two may present one AppId
    // (SharedCliServer rule (c)).

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

    private static void WriteManifest(string dir, Guid id, string name, Guid? dependsOn)
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
            dependencies = dependsOn is { } appId
                ? new[] { new { id = appId, name = "PkgDep App", publisher = "AL Runner", version = "1.0.0.0" } }
                : Array.Empty<object>(),
        }));
    }

    /// <summary>App/ (source), App.Test/ (source) and App.Test/.alpackages/App.app built from App/.</summary>
    private static (string App, string TestApp) Layout()
    {
        var root = TestScratch.Dir("al-runner-server-affected-pkgdep");
        var app = Path.Combine(root, "App");
        var testApp = Path.Combine(root, "App.Test");
        var appId = Guid.NewGuid();
        WriteManifest(app, appId, "PkgDep App", dependsOn: null);
        Directory.CreateDirectory(Path.Combine(app, "src"));
        File.WriteAllText(Path.Combine(app, "src", "Helper.Codeunit.al"), HelperSource(2));
        WriteManifest(testApp, Guid.NewGuid(), "PkgDep App Test", dependsOn: appId);
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
        var appId = JsonDocument.Parse(File.ReadAllText(Path.Combine(app, "app.json"))).RootElement.GetProperty("id").GetGuid();
        var symbols = JsonSerializer.SerializeToUtf8Bytes(new
        {
            AppId = appId, Name = "PkgDep App", Publisher = "AL Runner", Version = "1.0.0.0", RuntimeVersion = "14.0",
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
        var stderrMark = server.StdErrMark;
        var lines = await server.SendRequestStreamingAsync(Request(testApp), TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines) + "\n--- stderr ---\n" + server.StdErrSince(stderrMark);
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
        var server = await _fixture.GetAsync(new[] { "--no-cache" });

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
            if (server == 1)
                Assert.True(first.Status.GetValueOrDefault("CallsApp") == "pass", first.Raw);
            else
            {
                // #4979: the second process starts from the first one's persisted baseline, so the
                // environment key (package fingerprint included) has to be the same in both.
                Assert.False(first.ForcedFull, first.Raw);
                Assert.Equal(0, first.Ran);
            }
            var unchanged = await Send(s, testApp);
            Assert.False(unchanged.ForcedFull, $"server {server}: {unchanged.Raw}");
            Assert.Equal(0, unchanged.Ran);
            Assert.Equal(2, unchanged.Skipped);
        }
    }

    // #4979: the package replaced while no server runs. The next server finds the persisted baseline
    // under a different environment key; since #5028 it diffs the two packages per object and runs
    // the tests that reached the changed one (here the whole codeunit, by its isolation), with a warning.
    [SkippableFact]
    public async Task RebuiltPackage_BetweenServerProcesses_RunsTheCallerNamingTheEnvironmentDrift()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp) = Layout();
        var cache = TestScratch.Dir("al-runner-server-affected-pkgdep-persist-cache");
        await using (var first = await CliServer.StartAsync(new[] { "--cache", cache }))
            Assert.True((await Send(first, testApp)).Status.GetValueOrDefault("CallsApp") == "pass");

        File.WriteAllText(Path.Combine(app, "src", "Helper.Codeunit.al"), HelperSource(3));
        Package(app, testApp);

        await using var second = await CliServer.StartAsync(new[] { "--cache", cache });
        var afterRebuild = await Send(second, testApp);
        Assert.False(afterRebuild.ForcedFull, afterRebuild.Raw);
        Assert.Contains("\"environmentDrift\":{", afterRebuild.Raw, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"diffed\"", afterRebuild.Raw, StringComparison.Ordinal);
        Assert.Contains("Codeunit 60471 PkgDep Helper SX", afterRebuild.Raw, StringComparison.Ordinal);
        Assert.Equal(2, afterRebuild.Ran);
        Assert.True(afterRebuild.Status.GetValueOrDefault("CallsApp") == "fail", afterRebuild.Raw);
        Assert.Contains("the app returned 63", afterRebuild.Raw, StringComparison.Ordinal);
    }

    // Review of #4986 asked what runs when a request has compiled App/ as its own bundle and a later
    // [App.Test] request resolves the package. Until #5079 that request ran the source-compiled
    // module (#1892 reuse by AppId), so editing App/ without repackaging changed what ran. Since #5079
    // a module from an earlier request answers only for its own source: App.Test runs the package it
    // resolves, as a fresh server does, and an edit to App/ that is not packaged changes nothing.
    [SkippableFact]
    public async Task SourceCompiledModuleFromAnEarlierRequest_DoesNotAnswerForThePackage()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp) = Layout();
        var server = await _fixture.GetAsync(new[] { "--no-cache" });
        var plainApp = JsonSerializer.Serialize(new
        {
            command = "runTests", sourcePaths = new[] { app }, packagePaths = Array.Empty<string>(),
        });
        var plainTestApp = JsonSerializer.Serialize(new
        {
            command = "runTests", sourcePaths = new[] { testApp }, packagePaths = Array.Empty<string>(),
        });

        await server.SendRequestStreamingAsync(plainApp, TimeSpan.FromSeconds(180));
        var baseline = await Send(server, testApp);
        Assert.True(baseline.Status.GetValueOrDefault("CallsApp") == "pass", baseline.Raw);
        await Send(server, testApp);

        File.WriteAllText(Path.Combine(app, "src", "Helper.Codeunit.al"), HelperSource(3));
        await server.SendRequestStreamingAsync(plainApp, TimeSpan.FromSeconds(180));

        var afterEdit = await Send(server, testApp);
        Assert.DoesNotContain("the app returned 63", afterEdit.Raw, StringComparison.Ordinal);
        Assert.True(afterEdit.Status.GetValueOrDefault("CallsApp", "not run") != "fail", afterEdit.Raw);

        var lines = await server.SendRequestStreamingAsync(plainTestApp, TimeSpan.FromSeconds(180));
        var (events, summary) = ProtocolV2Streaming.Split(lines);
        var raw = string.Join(" | ", lines);
        Assert.True(summary.GetProperty("exitCode").GetInt32() == 0, raw);
        Assert.Contains(events, e => e.GetProperty("name").GetString()!.EndsWith(".CallsApp", StringComparison.Ordinal)
            && e.GetProperty("status").GetString() == "pass");
    }

    [SkippableFact]
    public async Task RebuiltPackage_SameVersion_RunsTheTestThatCallsIntoIt_AgainstTheNewCode()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp) = Layout();
        var server = await _fixture.GetAsync(new[] { "--no-cache" });

        var baseline = await Send(server, testApp);
        Assert.True(baseline.Status.GetValueOrDefault("CallsApp") == "pass", baseline.Raw);
        var unchanged = await Send(server, testApp);
        Assert.True(unchanged.Ran == 0, unchanged.Raw);

        File.WriteAllText(Path.Combine(app, "src", "Helper.Codeunit.al"), HelperSource(3));
        Package(app, testApp);

        // Same AppId and version, so only the package's content can tell the two apart. #5028: the
        // two builds are diffed per object, and the caller of the changed codeunit is selected.
        var afterRebuild = await Send(server, testApp);
        Assert.False(afterRebuild.ForcedFull, afterRebuild.Raw);
        Assert.Contains("\"mode\":\"diffed\"", afterRebuild.Raw, StringComparison.Ordinal);
        Assert.True(afterRebuild.Status.TryGetValue("CallsApp", out var status), afterRebuild.Raw);
        Assert.True(status == "fail", afterRebuild.Raw);
        Assert.Contains("the app returned 63", afterRebuild.Raw, StringComparison.Ordinal);
    }
}
