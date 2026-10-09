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

    private static string OtherSource(int factor) => $$"""
        codeunit 60472 "Drift Other SX"
        {
            procedure Twice(Value: Integer): Integer
            begin
                exit(Value * {{factor}});
            end;
        }
        """;

    // An enum no test uses: a changed object of a kind no recording holds, so the diff is approximate.
    private const string KindSource = """
        enum 60473 "Drift Kind SX"
        {
            value(0; First) { }
        }
        """;

    // #5076: a permission set of the dependency, and a test that reads it through Aggregate Permission Set.
    private static string PermissionSetSource(string caption) => $$"""
        permissionset 60474 "Drift Perm SX"
        {
            Assignable = true;
            Caption = '{{caption}}';
            Permissions = codeunit "Drift Helper SX" = X;
        }
        """;

    private const string PermTestsSource = """
        codeunit 60481 "Drift Perm Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure ReadsPermissionSet()
            var
                A: Record "Aggregate Permission Set";
            begin
                A.SetRange("Role ID", 'Drift Perm SX');
                if not A.FindFirst() then
                    Error('ReadsPermissionSet: the permission set was not found');
                if A.Name <> 'Drift Perm A' then
                    Error('ReadsPermissionSet: the caption was %1', A.Name);
            end;
        }
        """;

    // #5452: a profile of the dependency, and a test that reads it through All Profile.
    private static string ProfileSource(string caption) => $$"""
        profile "Drift Profile SX"
        {
            Caption = '{{caption}}';
            RoleCenter = "Drift RC SX";
        }
        """;

    private const string RoleCenterSource = """
        page 60475 "Drift RC SX"
        {
            PageType = RoleCenter;
            layout
            {
                area(RoleCenter)
                {
                }
            }
        }
        """;

    private const string ProfileTestsSource = """
        codeunit 60483 "Drift Profile Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure ReadsProfile()
            var
                P: Record "All Profile";
            begin
                P.SetRange("Profile ID", 'Drift Profile SX');
                if not P.FindFirst() then
                    Error('ReadsProfile: the profile was not found');
                if P.Caption <> 'Drift Profile A' then
                    Error('ReadsProfile: the caption was %1', P.Caption);
            end;
        }
        """;

    // #5088: a dependency's report and the reportextension of it, and a test that runs the report.
    private const string DriftReportSource = """
        report 60476 "Drift Report SX"
        {
            ProcessingOnly = true;
            dataset
            {
                dataitem(Int; Integer)
                {
                    DataItemTableView = where(Number = const(1));
                    column(Num; Number) { }
                }
            }
        }
        """;

    private static string ReportExtensionSource(string body) => $$"""
        reportextension 60477 "Drift Report Ext SX" extends "Drift Report SX"
        {
            trigger OnPreReport()
            begin
                {{body}}
            end;
        }
        """;

    private const string ReportTestsSource = """
        codeunit 60482 "Drift Report Tests SX"
        {
            Subtype = Test;

            [Test]
            procedure RunsReport()
            begin
                Report.Run(Report::"Drift Report SX", false, false);
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

    private static void WriteManifest(string dir, Guid id, string name, bool dependsOnApp, string version = "1.0.0.0")
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), JsonSerializer.Serialize(new
        {
            id,
            name,
            publisher = "AL Runner",
            version,
            platform = "1.0.0.0",
            runtime = "14.0",
            idRanges = new[] { new { from = 60470, to = 60489 } },
            dependencies = dependsOnApp
                ? new[] { new { id = AppId, name = "Drift App", publisher = "AL Runner", version = "1.0.0.0" } }
                : Array.Empty<object>(),
        }));
    }

    private static (string App, string TestApp, string Cache) Layout(string name, bool permissionSet = false, bool report = false, bool profile = false)
    {
        var root = TestScratch.Dir("al-runner-server-affected-drift-" + name);
        var app = Path.Combine(root, "App");
        var testApp = Path.Combine(root, "App.Test");
        WriteManifest(app, AppId, "Drift App", dependsOnApp: false);
        Directory.CreateDirectory(Path.Combine(app, "src"));
        File.WriteAllText(Path.Combine(app, "src", "Helper.Codeunit.al"), HelperSource(2));
        File.WriteAllText(Path.Combine(app, "src", "Other.Codeunit.al"), OtherSource(2));
        WriteManifest(testApp, TestAppId, "Drift App Test", dependsOnApp: true);
        File.WriteAllText(Path.Combine(testApp, "Tests.Codeunit.al"), TestsSource);
        if (permissionSet)
        {
            File.WriteAllText(Path.Combine(app, "src", "Perm.PermissionSet.al"), PermissionSetSource("Drift Perm A"));
            File.WriteAllText(Path.Combine(testApp, "PermTests.Codeunit.al"), PermTestsSource);
        }
        if (profile)
        {
            File.WriteAllText(Path.Combine(app, "src", "Profile.Profile.al"), ProfileSource("Drift Profile A"));
            File.WriteAllText(Path.Combine(app, "src", "RC.Page.al"), RoleCenterSource);
            File.WriteAllText(Path.Combine(testApp, "ProfileTests.Codeunit.al"), ProfileTestsSource);
        }
        if (report)
        {
            File.WriteAllText(Path.Combine(app, "src", "Report.Report.al"), DriftReportSource);
            File.WriteAllText(Path.Combine(app, "src", "ReportExt.ReportExt.al"), ReportExtensionSource("exit;"));
            File.WriteAllText(Path.Combine(testApp, "ReportTests.Codeunit.al"), ReportTestsSource);
        }
        Package(app, testApp, permissionSet ? "Drift Perm A" : null, report, profileCaption: profile ? "Drift Profile A" : null);
        return (app, testApp, Path.Combine(root, "cache"));
    }

    // Rebuilds App.app from App/, at App/app.json's version, replacing the previous build: the
    // second environment.
    private static void Package(string app, string testApp, string? permissionSetCaption = null, bool report = false, bool reportExtension = true,
        string? profileCaption = null)
    {
        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(app, "app.json"))!;
        var packages = Path.Combine(testApp, ".alpackages");
        Directory.CreateDirectory(packages);
        foreach (var old in Directory.GetFiles(packages, "*.app")) File.Delete(old);
        var version = identity.Version.ToString();
        object Codeunit(int id, string name) => new
        {
            Id = id, Name = name, Methods = new[] { new {
                Id = 1516892452, Name = "Twice", ReturnTypeDefinition = new { Name = "Integer" },
                Parameters = new[] { new { Name = "Value", TypeDefinition = new { Name = "Integer" } } }
            } },
        };
        var symbols = JsonSerializer.SerializeToUtf8Bytes(new
        {
            AppId, Name = "Drift App", Publisher = "AL Runner", Version = version, RuntimeVersion = "14.0",
            Codeunits = new[] { Codeunit(60471, "Drift Helper SX"), Codeunit(60472, "Drift Other SX") },
            PermissionSets = permissionSetCaption == null ? Array.Empty<object>() : new object[] { new {
                Id = 60474, Name = "Drift Perm SX",
                Properties = new[] {
                    new { Name = "Assignable", Value = "true" }, new { Name = "Caption", Value = permissionSetCaption },
                },
            } },
            Profiles = profileCaption == null ? Array.Empty<object>() : new object[] { new {
                Name = "Drift Profile SX",
                Properties = new[] { new { Name = "Caption", Value = profileCaption } },
            } },
            Reports = report ? new object[] { new { Id = 60476, Name = "Drift Report SX", RequestPage = new { Id = 0, Name = "RequestOptionsPage" } } } : Array.Empty<object>(),
            ReportExtensions = report && reportExtension ? new object[] { new { Id = 60477, Name = "Drift Report Ext SX", Target = "Drift Report SX", RequestPage = new { ControlChanges = Array.Empty<object>() } } } : Array.Empty<object>(),
        });
        InProcessAppPackager.EmitAppPackageToFile(app, identity,
            Path.Combine(packages, $"AL Runner_Drift App_{version}.app"), symbols);
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

    private static async Task RecordBaseline(string testApp, string cache, int tests = 3)
    {
        await using var first = await CliServer.StartAsync(new[] { "--cache", cache });
        var baseline = await Send(first, testApp);
        Assert.True(baseline.Status.Count == tests && baseline.Status.Values.All(s => s == "pass"), baseline.Raw);
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

    // Review of #5073, findings 1 and 2. Drift 1 changes a codeunit and adds an enum no recording
    // holds: approximate, yet the changed codeunit's caller still runs. The tests drift 1 skipped keep
    // their record from the first environment, so drift 2, changing another codeunit, still diffs them
    // against it and runs that codeunit's caller.
    [SkippableFact]
    public async Task ApproximateDrift_StillSelectsWhatItResolved_AndTheNextDriftStillDiffsTheSkippedTests()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp, cache) = Layout("two-drifts");
        await RecordBaseline(testApp, cache);

        File.WriteAllText(Path.Combine(app, "src", "Kind.Enum.al"), KindSource);
        ChangeHelper(app, testApp);
        await using (var second = await CliServer.StartAsync(new[] { "--cache", cache }))
        {
            var first = await Send(second, testApp);
            Assert.False(first.ForcedFull, first.Raw);
            var d = first.Drift ?? throw new Xunit.Sdk.XunitException("no environmentDrift: " + first.Raw);
            Assert.Equal("approximate", d.GetProperty("mode").GetString());
            Assert.Contains("Enum 60473 Drift Kind SX changed", d.GetProperty("reason").GetString());
            Assert.Equal(new[] { "CallsHelper" }, first.Status.Keys);
            Assert.True(first.Status["CallsHelper"] == "fail", first.Raw);
            Assert.Contains("the app returned 63", first.Raw, StringComparison.Ordinal);
        }

        File.WriteAllText(Path.Combine(app, "src", "Other.Codeunit.al"), OtherSource(5));
        Package(app, testApp);
        await using var third = await CliServer.StartAsync(new[] { "--cache", cache });
        var second2 = await Send(third, testApp);
        Assert.False(second2.ForcedFull, second2.Raw);
        // #5057: CallsHelper failed last time, so it wrote the last error, and it is the nearest such
        // writer before the changed CallsOther, which could now read it.
        Assert.Equal(new[] { "CallsHelper", "CallsOther" }, second2.Status.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(second2.Status["CallsOther"] == "fail", second2.Raw);
        Assert.Contains("CallsOther: the app returned 105", second2.Raw, StringComparison.Ordinal);
        var d2 = second2.Drift ?? throw new Xunit.Sdk.XunitException("no environmentDrift: " + second2.Raw);
        Assert.Contains("Codeunit 60472 Drift Other SX",
            d2.GetProperty("objects").EnumerateArray().Select(o => o.GetString()));
    }

    // Review of #5073, finding 3. A new BC build moves the resolved version of every Microsoft
    // dependency while a test app's app.json, which names them through $(app_*) placeholders, stays the
    // same. The dependency set a module was compiled against then differs, and must not force a full run.
    [SkippableFact]
    public async Task DependencyVersionMoves_TheRequestsAppJsonDoesNot_DiffsInsteadOfAFullRun()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp, cache) = Layout("version");
        await RecordBaseline(testApp, cache);

        WriteManifest(app, AppId, "Drift App", dependsOnApp: false, version: "1.0.0.1");
        ChangeHelper(app, testApp);
        await using var second = await CliServer.StartAsync(new[] { "--cache", cache });
        var drifted = await Send(second, testApp);
        Assert.False(drifted.ForcedFull, drifted.Raw);
        Assert.Equal(new[] { "CallsHelper" }, drifted.Status.Keys);
        Assert.Contains("the app returned 63", drifted.Raw, StringComparison.Ordinal);
        Assert.Equal("diffed", (drifted.Drift ?? throw new Xunit.Sdk.XunitException(drifted.Raw)).GetProperty("mode").GetString());
    }

    // #5076: a dependency's changed permission set is attributed to the tests that read the permission
    // tables, so the diff stays exact (a minor BC bump changes some), and only those tests run.
    [SkippableFact]
    public async Task PermissionSetDiffers_SelectsTheTestThatReadsPermissionTables_AndTheDiffStaysExact()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp, cache) = Layout("permission-set", permissionSet: true);
        await RecordBaseline(testApp, cache, tests: 4);

        File.WriteAllText(Path.Combine(app, "src", "Perm.PermissionSet.al"), PermissionSetSource("Drift Perm B"));
        Package(app, testApp, "Drift Perm B");

        await using var second = await CliServer.StartAsync(new[] { "--cache", cache });
        var drifted = await Send(second, testApp);
        Assert.False(drifted.ForcedFull, drifted.Raw);
        Assert.Equal(new[] { "ReadsPermissionSet" }, drifted.Status.Keys);
        Assert.True(drifted.Status["ReadsPermissionSet"] == "fail", drifted.Raw);
        Assert.Contains("the caption was Drift Perm B", drifted.Raw, StringComparison.Ordinal);
        var d = drifted.Drift ?? throw new Xunit.Sdk.XunitException("no environmentDrift: " + drifted.Raw);
        Assert.Equal("diffed", d.GetProperty("mode").GetString());
        Assert.Equal(new[] { "PermissionSet 60474 Drift Perm SX" },
            d.GetProperty("objects").EnumerateArray().Select(o => o.GetString()));
    }

    // #5088: a dependency's changed reportextension is attributed to the tests that ran its base report,
    // so the diff stays exact and only those tests run.
    [SkippableFact]
    public async Task ReportExtensionDiffers_SelectsTheTestThatRanItsBaseReport_AndTheDiffStaysExact()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp, cache) = Layout("report-extension", report: true);
        await RecordBaseline(testApp, cache, tests: 4);

        File.WriteAllText(Path.Combine(app, "src", "ReportExt.ReportExt.al"), ReportExtensionSource("Error('Drift Ext B');"));
        Package(app, testApp, report: true);

        await using var second = await CliServer.StartAsync(new[] { "--cache", cache });
        var drifted = await Send(second, testApp);
        Assert.False(drifted.ForcedFull, drifted.Raw);
        Assert.Equal(new[] { "RunsReport" }, drifted.Status.Keys);
        Assert.True(drifted.Status["RunsReport"] == "fail", drifted.Raw);
        Assert.Contains("Drift Ext B", drifted.Raw, StringComparison.Ordinal);
        var d = drifted.Drift ?? throw new Xunit.Sdk.XunitException("no environmentDrift: " + drifted.Raw);
        Assert.Equal("diffed", d.GetProperty("mode").GetString());
        Assert.Equal(new[] { "ReportExtension 60477 Drift Report Ext SX" },
            d.GetProperty("objects").EnumerateArray().Select(o => o.GetString()));
    }

    // The removed extension is in the new environment's registry no more, and a dependency's extension
    // instance is no key of a kind the diff selects on, so only the recording names its base report.
    [SkippableFact]
    public async Task ReportExtensionRemoved_SelectsTheTestThatRanItsBaseReport_FromTheRecording()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp, cache) = Layout("report-extension-removed", report: true);
        await RecordBaseline(testApp, cache, tests: 4);

        File.Delete(Path.Combine(app, "src", "ReportExt.ReportExt.al"));
        Package(app, testApp, report: true, reportExtension: false);

        await using var second = await CliServer.StartAsync(new[] { "--cache", cache });
        var drifted = await Send(second, testApp);
        Assert.False(drifted.ForcedFull, drifted.Raw);
        Assert.Equal(new[] { "RunsReport" }, drifted.Status.Keys);
        Assert.True(drifted.Status["RunsReport"] == "pass", drifted.Raw);
        var d = drifted.Drift ?? throw new Xunit.Sdk.XunitException("no environmentDrift: " + drifted.Raw);
        Assert.Equal("diffed", d.GetProperty("mode").GetString());
        Assert.Equal(new[] { "ReportExtension 60477 Drift Report Ext SX" },
            d.GetProperty("objects").EnumerateArray().Select(o => o.GetString()));
    }

    // #5452: a dependency's changed profile is attributed to the tests that read All Profile, so the diff
    // stays exact (a minor BC bump changes some), and only those tests run. The other tests of the
    // bundle do not read it and are not selected.
    [SkippableFact]
    public async Task ProfileDiffers_SelectsTheTestThatReadsAllProfile_AndTheDiffStaysExact()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp, cache) = Layout("profile", profile: true);
        await RecordBaseline(testApp, cache, tests: 4);

        File.WriteAllText(Path.Combine(app, "src", "Profile.Profile.al"), ProfileSource("Drift Profile B"));
        Package(app, testApp, profileCaption: "Drift Profile B");

        await using var second = await CliServer.StartAsync(new[] { "--cache", cache });
        var drifted = await Send(second, testApp);
        Assert.False(drifted.ForcedFull, drifted.Raw);
        Assert.Equal(new[] { "ReadsProfile" }, drifted.Status.Keys);
        Assert.True(drifted.Status["ReadsProfile"] == "fail", drifted.Raw);
        Assert.Contains("the caption was Drift Profile B", drifted.Raw, StringComparison.Ordinal);
        var d = drifted.Drift ?? throw new Xunit.Sdk.XunitException("no environmentDrift: " + drifted.Raw);
        Assert.Equal("diffed", d.GetProperty("mode").GetString());
        Assert.Equal(new[] { "Profile Drift Profile SX" },
            d.GetProperty("objects").EnumerateArray().Select(o => o.GetString()));
    }

    [SkippableFact]
    public async Task NoPerObjectRecord_UsesTheBaselineAsIs_AndSaysItIsApproximate()
    {
        TestArtifacts.SkipIfMissing();
        var (app, testApp, cache) = Layout("approximate");
        await RecordBaseline(testApp, cache);

        // A baseline with no record of its environment's objects (one written before #5028 had none;
        // such a file is now refused on its schema, so the record is removed from a current one).
        var file = Assert.Single(Directory.GetFiles(Path.Combine(cache, AffectedBaselineStore.CacheName), "*.json"));
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json.Remove("EnvApps");
        foreach (var bundle in json["Bundles"]!.AsObject())
        {
            bundle.Value!.AsObject().Remove("Envs");
            bundle.Value!.AsObject().Remove("TestEnv");
        }
        File.WriteAllText(file, json.ToJsonString());
        ChangeHelper(app, testApp);

        await using var second = await CliServer.StartAsync(new[] { "--cache", cache });
        var mark = second.StdErrMark;
        var drifted = await Send(second, testApp);
        Assert.False(drifted.ForcedFull, drifted.Raw);
        Assert.Equal(0, drifted.Ran);
        Assert.Equal(3, drifted.Skipped);
        var d = drifted.Drift ?? throw new Xunit.Sdk.XunitException("no environmentDrift: " + drifted.Raw);
        Assert.Equal("approximate", d.GetProperty("mode").GetString());
        Assert.Contains("no per-object record", d.GetProperty("reason").GetString());
        // The warning is printed on stderr, read asynchronously: wait for it (#5100).
        await second.StdErrSinceAsync(mark, "Selection is APPROXIMATE");
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
