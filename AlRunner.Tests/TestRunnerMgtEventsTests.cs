// TestRunnerMgtEventsTests — #4813: the runner raises Microsoft's "Test Runner - Mgt" (130454)
// per-codeunit and per-method events when the Test Runner app is loaded, so Microsoft's own
// subscriber 130453 "ALTestRunner Reset Environment" clears the application areas and LastError
// before each test and puts WorkDate back after each codeunit — and a suite's own subscribers
// see the arguments BC passes and can Skip a test.
//
// The plain-BC half (a test run through Microsoft's Test Runner starts with every area enabled)
// is corpus codeunit 67552 in corpus PR #467. What this pins is the runner's raising of the
// events, under every --isolation mode, and that a suite which does not declare the Test Runner
// app still runs through it when the caches hold it, as on a service tier (#4816). OnBeforeTestMethodRun has five parameters in Test Runner 27.x/28.0 and gains `var Skip`
// in 28.1, and each CI leg loads its own artifact's Test Runner, so the real-app probes are
// version-neutral; both publisher shapes, Skip, and the refusals are pinned on source-compiled
// stand-ins for 130454/130453 that carry Microsoft's object names. No Base Application dependency (.claude/rules/no-base-app-in-csharp-tests.md): the AL
// raises Error() carrying what it observed and the runner output is the assertion.
// Skips (not passes) when no Test Runner package is provisioned on the box; fails on CI.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

public sealed class TestRunnerMgtEventsTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private sealed record Provisioned(string TestApps, string PlatformApps, AppManifest TestRunner);

    // One location holding both Microsoft_Test Runner.app and System.app, never mixed across
    // versions — the same rule as LibraryAssertPlatformlessConsumerTests.FindProvisionedDirs.
    private static Provisioned? FindProvisionedDirs()
    {
        var home = TestArtifacts.HomeDir();
        var candidates = new List<(string TestApps, string PlatformApps)>();
        if (home != null)
            candidates.Add((Path.Combine(home, ".al-runner", "test-apps"), Path.Combine(home, ".al-runner", "platform-apps")));
        var root = AlRunner.Infrastructure.BcArtifacts.ArtifactsRootDir;
        if (Directory.Exists(root))
            foreach (var ver in Directory.EnumerateDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal))
                candidates.Add((Path.Combine(ver, "test-apps"), Path.Combine(ver, "platform-apps")));

        foreach (var c in candidates)
        {
            var path = Path.Combine(c.TestApps, "Microsoft_Test Runner.app");
            if (!File.Exists(path) || !File.Exists(Path.Combine(c.PlatformApps, "System.app"))) continue;
            var manifest = AppLoader.ReadManifest(path);
            if (manifest != null) return new Provisioned(c.TestApps, c.PlatformApps, manifest);
        }
        return null;
    }

    private static Provisioned RequireProvisioned()
    {
        TestArtifacts.SkipIfMissing();
        var dirs = FindProvisionedDirs();
        if (dirs == null && TestArtifacts.RunningOnCi)
            Assert.Fail("no Microsoft_Test Runner.app + System.app pair in one provisioned location; "
                + "CI provisions both (al-runner provision --test-apps --platform-apps).");
        TestArtifacts.SkipIf(dirs == null,
            "no Microsoft_Test Runner.app + System.app pair in one provisioned location on this box.");
        return dirs!;
    }

    private static string WriteBundle(string name, AppManifest? testRunner, string al, string target = "Cloud")
    {
        var root = TestScratch.Dir(name);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        var dependency = testRunner == null ? "" :
            $$"""{ "id": "{{testRunner.AppId}}", "name": "{{testRunner.Name}}", "publisher": "{{testRunner.Publisher}}", "version": "{{testRunner.Version}}" }""";
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "b4813000-0000-4000-8000-000000004813",
          "name": "TestRunnerEvents4813",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [ {{dependency}} ],
          "platform": "27.0.0.0",
          "idRanges": [ { "from": 64810, "to": 64819 }, { "from": 130450, "to": 130459 } ],
          "runtime": "14.0",
          "target": "{{target}}"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Probe.al"), al);
        return root;
    }

    private const string ResetProbes = """
        codeunit 64811 "TRE Reset Tests"
        {
            Subtype = Test;

            trigger OnRun()
            begin
                ApplicationArea('#Basic,#TREProbe');
            end;

            [Test]
            procedure A_FirstTestSeesNoArea()
            begin
                if ApplicationArea() <> '' then
                    Error('TRE1 FAIL: first test saw [%1]', ApplicationArea());
            end;

            [Test]
            procedure B_LeavesLastErrorAndWorkDate()
            begin
                WorkDate(20300115D);
                asserterror Error('TRE-LEFT-BEHIND');
                if GetLastErrorText() <> 'TRE-LEFT-BEHIND' then
                    Error('TRE2 FAIL: setup read [%1]', GetLastErrorText());
            end;

            [Test]
            procedure C_StartsWithLastErrorCleared()
            begin
                if GetLastErrorText() <> '' then
                    Error('TRE3 FAIL: next test saw last error [%1]', GetLastErrorText());
            end;

            [Test]
            procedure E_AfterEventCarriesTheOutcome()
            begin
            end;
        }

        codeunit 64812 "TRE Next Codeunit Tests"
        {
            Subtype = Test;

            [Test]
            procedure F_WorkDateIsPutBack()
            begin
                if WorkDate() <> 20200110D then
                    Error('TRE5 FAIL: next codeunit saw WorkDate [%1], not the 01/10/20 the run started with', WorkDate());
            end;
        }

        codeunit 64813 "TRE Subscribers"
        {
            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Test Runner - Mgt", 'OnAfterTestMethodRun', '', false, false)]
            local procedure ReportOne(CodeunitID: Integer; CodeunitName: Text[30]; FunctionName: Text[128]; IsSuccess: Boolean; var CurrentTestMethodLine: Record "Test Method Line")
            begin
                if FunctionName = 'E_AfterEventCarriesTheOutcome' then
                    Error('TRE-AFTER %1|%2|%3|%4|%5', CodeunitID, CodeunitName, FunctionName, IsSuccess, CurrentTestMethodLine."Function");
            end;
        }

        // The WorkDate in force when the run starts is not Today, so a lost 130453.Initialize
        // (CurrentWorkDate = 0D, and WorkDate(0D) answers Today) is told apart from a kept one.
        codeunit 64814 "TRE Install"
        {
            Subtype = Install;

            trigger OnInstallAppPerCompany()
            begin
                WorkDate(20200110D);
            end;
        }
        """;

    private static (string Output, int Exit) RunRunner(string bundle, Provisioned dirs, params string[] extra)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --no-cache --no-auto-provision --show-pass");
        args.Append($" --package-cache \"{dirs.TestApps}\" --package-cache \"{dirs.PlatformApps}\"");
        foreach (var e in extra) args.Append(" \"").Append(e).Append('"');
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static void HasLine(string output, string outcome, string method) =>
        Assert.True(Regex.IsMatch(output, $@"(?m)^{outcome}\s+\S*\b{Regex.Escape(method)}\b"),
            $"expected a {outcome} line for {method}. Output:\n{output}");

    private static void Lacks(string output, string s) =>
        Assert.False(output.Contains(s, StringComparison.Ordinal), $"did not expect [{s}]. Output:\n{output}");

    [SkippableTheory]
    [InlineData("codeunit")]
    [InlineData("test")]
    [InlineData("disabled")]
    public void TestRunnerLoaded_MicrosoftsResetSubscriberAndSuiteSubscribersRun(string isolation)
    {
        var dirs = RequireProvisioned();
        var bundle = WriteBundle($"al-runner-test-runner-events-4813-{isolation}", dirs.TestRunner, ResetProbes);

        var (output, _) = RunRunner(bundle, dirs, "--isolation", isolation);

        // 130453 on OnBeforeTestMethodRun: ApplicationArea('') and ClearLastError().
        HasLine(output, "PASS", "A_FirstTestSeesNoArea");
        HasLine(output, "PASS", "B_LeavesLastErrorAndWorkDate");
        HasLine(output, "PASS", "C_StartsWithLastErrorCleared");
        // OnAfterTestMethodRun carries id, object name, AL name, outcome, and the line's Function.
        Assert.Contains("TRE-AFTER 64811|TRE Reset Tests|E_AfterEventCarriesTheOutcome|Yes|E_AfterEventCarriesTheOutcome",
            output, StringComparison.Ordinal);
        // 130453 on OnAfterCodeunitRun: WorkDate(CurrentWorkDate), recorded by Initialize.
        HasLine(output, "PASS", "F_WorkDateIsPutBack");
        foreach (var marker in new[] { "TRE1 FAIL", "TRE2 FAIL", "TRE3 FAIL", "TRE5 FAIL" })
            Lacks(output, marker);
    }

    // #4816: a suite that does not declare Test Runner still runs through it, as on a service
    // tier, whenever the package caches hold the app.
    [SkippableFact]
    public void TestRunnerNotDeclared_IsLoadedAsTheInstalledTestTool()
    {
        var dirs = RequireProvisioned();
        var bundle = WriteBundle("al-runner-test-runner-events-4816-undeclared", null, """
            codeunit 64811 "TRE Undeclared Tests"
            {
                Subtype = Test;

                trigger OnRun()
                begin
                    ApplicationArea('#Basic,#TREProbe');
                end;

                [Test]
                procedure A_FirstTestSeesNoArea()
                begin
                    if ApplicationArea() <> '' then
                        Error('TRE6 FAIL: undeclared Test Runner, first test saw [%1]', ApplicationArea());
                end;

                [Test]
                procedure B_LeavesLastError()
                begin
                    asserterror Error('TRE-LEFT-BEHIND');
                end;

                [Test]
                procedure C_StartsWithLastErrorCleared()
                begin
                    if GetLastErrorText() <> '' then
                        Error('TRE7 FAIL: undeclared Test Runner, next test saw last error [%1]', GetLastErrorText());
                end;
            }
            """);

        var (output, exit) = RunRunner(bundle, dirs);

        HasLine(output, "PASS", "A_FirstTestSeesNoArea");
        HasLine(output, "PASS", "C_StartsWithLastErrorCleared");
        Lacks(output, "TRE6 FAIL");
        Lacks(output, "TRE7 FAIL");
        Assert.Equal(0, exit);
    }

    [SkippableFact]
    public void WithInstalledTestTool_AddsTheRootOnlyWhenTheCachesHoldIt_AndNobodyNamedItYet()
    {
        var dirs = RequireProvisioned();
        var empty = TestScratch.Dir("al-runner-test-runner-4816-empty-cache");
        if (Directory.Exists(empty)) Directory.Delete(empty, recursive: true);
        Directory.CreateDirectory(empty);
        var holding = new DependencyResolver(new[] { dirs.TestApps });
        var tool = ProgramSupport.InstalledTestTool;
        var other = new DependencyRef(Guid.NewGuid(), "Other", "Someone", new Version(1, 0, 0, 0));

        var added = ProgramSupport.WithInstalledTestTool(new List<DependencyRef> { other }, Array.Empty<string>(), holding);
        Assert.Equal(new[] { other.AppId, tool.AppId }, added.Select(r => r.AppId));
        Assert.True(added[1].Optional);

        var absent = ProgramSupport.WithInstalledTestTool(
            new List<DependencyRef> { other }, Array.Empty<string>(), new DependencyResolver(new[] { empty }));
        Assert.Equal(new[] { other.AppId }, absent.Select(r => r.AppId));

        var declared = new DependencyRef(dirs.TestRunner.AppId, dirs.TestRunner.Name, dirs.TestRunner.Publisher, dirs.TestRunner.Version);
        var kept = ProgramSupport.WithInstalledTestTool(new List<DependencyRef> { declared }, Array.Empty<string>(), holding);
        Assert.Single(kept);

        // Only a codeunit the suite itself defines at 130453/130454 keeps Microsoft's app out; an
        // id range covering 130454 with nothing there does not (Microsoft's Email - SMTP test apps
        // declare 100000-150000).
        var defines130454 = WriteApp("defines-130454", "codeunit 130454 \"My Test Mgt\" { }");
        var defines130453 = WriteApp("defines-130453", "codeunit 130453 \"My Reset\" { }");
        var rangeOnly = WriteApp("range-only", "// codeunit 130454 is not declared here\ncodeunit 130455 \"Beside\" { }");
        Assert.Equal(new[] { other.AppId },
            ProgramSupport.WithInstalledTestTool(new List<DependencyRef> { other }, new[] { defines130454 }, holding).Select(r => r.AppId));
        Assert.Equal(new[] { other.AppId },
            ProgramSupport.WithInstalledTestTool(new List<DependencyRef> { other }, new[] { defines130453 }, holding).Select(r => r.AppId));
        Assert.Equal(new[] { other.AppId, tool.AppId },
            ProgramSupport.WithInstalledTestTool(new List<DependencyRef> { other }, new[] { rangeOnly }, holding).Select(r => r.AppId));

        string WriteApp(string name, string al)
        {
            var dir = Path.Combine(empty, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Objects.al"), al);
            var appJson = Path.Combine(dir, "app.json");
            File.WriteAllText(appJson, $$"""{ "id": "{{Guid.NewGuid()}}", "idRanges": [ { "from": 100000, "to": 150000 } ] }""");
            return appJson;
        }
    }

    // Stand-ins for Microsoft's 130454/130453 and table 130450, compiled from source inside the
    // suite's own app, so a leg's own Test Runner version does not decide which shape is tested.
    private static string StandIns(string mgtName, bool withSkip, bool withResetEnvironment) => $$"""
        table 130450 "Test Method Line"
        {
            fields
            {
                field(1; "Test Suite"; Code[10]) { }
                field(2; "Line No."; Integer) { }
                field(3; "Line Type"; Option) { OptionMembers = "Codeunit","Function"; }
                field(4; "Test Codeunit"; Integer) { }
                field(5; Name; Text[128]) { }
                field(6; "Function"; Text[128]) { }
                field(7; Run; Boolean) { }
                field(8; Result; Option) { OptionMembers = " ",Failure,Success,Skipped; }
            }
            keys { key(PK; "Test Suite", "Line No.") { Clustered = true; } }
        }

        codeunit 130454 "{{mgtName}}"
        {
            [IntegrationEvent(false, false)]
            local procedure OnBeforeCodeunitRun(var TestMethodLine: Record "Test Method Line")
            begin
            end;

            [IntegrationEvent(false, false)]
            local procedure OnAfterCodeunitRun(var TestMethodLine: Record "Test Method Line")
            begin
            end;

            [IntegrationEvent(false, false)]
            local procedure OnBeforeTestMethodRun(var CurrentTestMethodLine: Record "Test Method Line"; CodeunitID: Integer; CodeunitName: Text[30]; FunctionName: Text[128]; FunctionTestPermissions: TestPermissions{{(withSkip ? "; var Skip: Boolean" : "")}})
            begin
            end;

            [IntegrationEvent(false, false)]
            local procedure OnAfterTestMethodRun(var CurrentTestMethodLine: Record "Test Method Line"; CodeunitID: Integer; CodeunitName: Text[30]; FunctionName: Text[128]; FunctionTestPermissions: TestPermissions; IsSuccess: Boolean)
            begin
            end;
        }
        {{(withResetEnvironment ? ResetEnvironmentStandIn : "")}}
        """;

    private const string ResetEnvironmentStandIn = """
        codeunit 130453 "ALTestRunner Reset Environment"
        {
            SingleInstance = true;

            procedure Initialize()
            begin
            end;
        }
        """;

    // Subscribes to the stand-in's OnBeforeTestMethodRun in the shape it has; the test body
    // reads back the area the subscriber set, so a raised event is visible and a missing one is not.
    private static string StandInProbes(bool withSkip) => $$"""
        codeunit 64811 "TRS Probe Tests"
        {
            Subtype = Test;

            [Test]
            procedure A_BeforeEventRanForThisTest()
            begin
                if ApplicationArea() <> '#TRSRaised' then
                    Error('TRS1 FAIL: OnBeforeTestMethodRun was not raised for this test; areas [%1]', ApplicationArea());
            end;
        {{(withSkip ? SkippedTest : "")}}
        }

        codeunit 64813 "TRS Subscribers"
        {
            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Test Runner - Mgt", 'OnBeforeTestMethodRun', '', false, false)]
            local procedure MarkAndSkip(CodeunitID: Integer; FunctionName: Text[128]; var CurrentTestMethodLine: Record "Test Method Line"{{(withSkip ? "; var Skip: Boolean" : "")}})
            begin
                if (CodeunitID = 64811) and (CurrentTestMethodLine."Test Codeunit" = 64811) and (FunctionName = 'A_BeforeEventRanForThisTest') then
                    ApplicationArea('#TRSRaised');
                {{(withSkip ? "if FunctionName = 'D_SkippedBySubscriber' then Skip := true;" : "")}}
            end;
        }
        """;

    private const string SkippedTest = """

            [Test]
            procedure D_SkippedBySubscriber()
            begin
                Error('TRS4 FAIL: a test the subscriber skipped ran');
            end;
        """;

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void StandInTestRunner_BothPublisherShapes_AreRaised_AndSkipIsHonouredWhereItExists(bool withSkip)
    {
        var dirs = RequireProvisioned();
        var bundle = WriteBundle($"al-runner-test-runner-events-4813-standin-{(withSkip ? 6 : 5)}", null,
            StandIns("Test Runner - Mgt", withSkip, withResetEnvironment: true) + StandInProbes(withSkip));

        var (output, _) = RunRunner(bundle, dirs);

        HasLine(output, "PASS", "A_BeforeEventRanForThisTest");
        Lacks(output, "TRS1 FAIL");
        Lacks(output, "BcShapeGap");
        Assert.Contains("not loading Microsoft's Test Runner app by default, because this suite defines codeunit", output, StringComparison.Ordinal);
        if (withSkip)
        {
            HasLine(output, "SKIP", "D_SkippedBySubscriber");
            Lacks(output, "TRS4 FAIL");
        }
    }

    [SkippableFact]
    public void StandInTestRunner_WithoutResetEnvironment_IsRefused()
    {
        var dirs = RequireProvisioned();
        var bundle = WriteBundle("al-runner-test-runner-events-4813-standin-no-130453", null,
            StandIns("Test Runner - Mgt", withSkip: true, withResetEnvironment: false) + StandInProbes(withSkip: true));

        var (output, exit) = RunRunner(bundle, dirs);

        Assert.True(Regex.IsMatch(output, @"(?m)^ERROR\s+.*<OnBeforeCodeunitRun>"),
            $"expected an ERROR entry for <OnBeforeCodeunitRun>. Output:\n{output}");
        Assert.Contains("is loaded but codeunit 130453", output, StringComparison.Ordinal);
        Assert.NotEqual(0, exit);
    }

    [SkippableFact]
    public void ASuitesOwnCodeunit130454_WithAnotherName_IsLeftAlone()
    {
        var dirs = RequireProvisioned();
        // Same publishers, but the object is not named "Test Runner - Mgt", so nothing is raised
        // and the subscriber (bound by id) never sets the area.
        var bundle = WriteBundle("al-runner-test-runner-events-4813-own-130454", null,
            StandIns("My Own Test Mgt", withSkip: false, withResetEnvironment: true)
            + StandInProbes(withSkip: false).Replace("Codeunit::\"Test Runner - Mgt\"", "Codeunit::\"My Own Test Mgt\"")
                .Replace("if ApplicationArea() <> '#TRSRaised' then", "if ApplicationArea() <> '' then"));

        var (output, _) = RunRunner(bundle, dirs);

        HasLine(output, "PASS", "A_BeforeEventRanForThisTest");
        Lacks(output, "TRS1 FAIL");
    }

    // #4842: BC raises OnBeforeTestMethodRun/OnAfterTestMethodRun from the test runner's
    // OnBeforeTestRun/OnAfterTestRun triggers, which NavTestCodeunit.DoRunAsync calls between
    // EnterTestCodeunit and LeaveTestCodeunit, so NavTestExecution.IsInTestMode() is true there.
    // 130454.RunTests raises OnBeforeCodeunitRun/OnAfterCodeunitRun around CODEUNIT.Run, outside
    // that scope, so it is false there. The probe is the gate Permissions Mock 131006 hits.
    private const string InTestProbes = """
        dotnet
        {
            assembly("Microsoft.Dynamics.Nav.PermissionTestHelper")
            {
                type("Microsoft.Dynamics.Nav.Runtime.PermissionTestHelper"; "PermissionTestHelper") { }
            }
        }

        codeunit 64811 "TRP Probe Tests"
        {
            Subtype = Test;

            [Test]
            procedure A_FirstTest()
            begin
            end;

            [Test]
            procedure B_SecondTest()
            begin
            end;

            // Skipped by the subscriber and last in the codeunit: a scope left open on the Skip
            // path would still be open for OnAfterCodeunitRun, which the TRP-CU-AFTER probe catches.
            [Test]
            procedure C_SkippedLast()
            begin
                Error('TRP-SKIP FAIL: a test the subscriber skipped ran');
            end;
        }

        codeunit 64813 "TRP Subscribers"
        {
            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Test Runner - Mgt", 'OnBeforeCodeunitRun', '', false, false)]
            local procedure BeforeCodeunit()
            begin
                if TryAddPermissionSet() then
                    Error('TRP-CU-BEFORE FAIL: IsInTestMode() was true in OnBeforeCodeunitRun');
            end;

            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Test Runner - Mgt", 'OnBeforeTestMethodRun', '', false, false)]
            local procedure BeforeMethod(FunctionName: Text[128]; var Skip: Boolean)
            begin
                if not TryAddPermissionSet() then
                    Error('TRP-M-BEFORE FAIL %1: %2', FunctionName, GetLastErrorText());
                if FunctionName = 'C_SkippedLast' then
                    Skip := true;
            end;

            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Test Runner - Mgt", 'OnAfterTestMethodRun', '', false, false)]
            local procedure AfterMethod(FunctionName: Text[128])
            begin
                if not TryAddPermissionSet() then
                    Error('TRP-M-AFTER FAIL %1: %2', FunctionName, GetLastErrorText());
            end;

            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Test Runner - Mgt", 'OnAfterCodeunitRun', '', false, false)]
            local procedure AfterCodeunit()
            begin
                if TryAddPermissionSet() then
                    Error('TRP-CU-AFTER FAIL: IsInTestMode() was true in OnAfterCodeunitRun');
            end;

            [TryFunction]
            local procedure TryAddPermissionSet()
            var
                Helper: DotNet PermissionTestHelper;
            begin
                Helper := Helper.PermissionTestHelper();
                Helper.AddEffectivePermissionSet('TRPPROBE');
                Helper.Clear();
            end;
        }
        """;

    [SkippableTheory]
    [InlineData("codeunit")]
    [InlineData("test")]
    public void MethodEvents_AreRaisedInsideTheTestCodeunitScope_CodeunitEventsOutsideIt(string isolation)
    {
        var dirs = RequireProvisioned();
        var bundle = WriteBundle($"al-runner-test-runner-events-4842-{isolation}", null,
            StandIns("Test Runner - Mgt", withSkip: true, withResetEnvironment: true) + InTestProbes,
            target: "OnPrem");

        var (output, _) = RunRunner(bundle, dirs, "--isolation", isolation);

        HasLine(output, "PASS", "A_FirstTest");
        HasLine(output, "PASS", "B_SecondTest");
        HasLine(output, "SKIP", "C_SkippedLast");
        foreach (var marker in new[] { "TRP-M-BEFORE FAIL", "TRP-M-AFTER FAIL", "TRP-CU-BEFORE FAIL", "TRP-CU-AFTER FAIL", "TRP-SKIP FAIL" })
            Lacks(output, marker);
        Lacks(output, "BcShapeGap");
    }
}
