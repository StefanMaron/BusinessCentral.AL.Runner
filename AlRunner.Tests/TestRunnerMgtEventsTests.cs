// TestRunnerMgtEventsTests — #4813: the runner raises Microsoft's "Test Runner - Mgt" (130454)
// per-codeunit and per-method events when the Test Runner app is loaded, so Microsoft's own
// subscriber 130453 "ALTestRunner Reset Environment" clears the application areas and LastError
// before each test and puts WorkDate back after each codeunit — and a suite's own subscribers
// see the arguments BC passes and can Skip a test.
//
// The plain-BC half (a test run through Microsoft's Test Runner starts with every area enabled)
// is corpus codeunit 67552 in corpus PR #467. What this pins is the runner's raising of the
// events, under every --isolation mode, and that a suite WITHOUT the Test Runner app is left
// alone. No Base Application dependency (.claude/rules/no-base-app-in-csharp-tests.md): the AL
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

    private static string WriteBundle(string name, AppManifest? testRunner, string al)
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
          "idRanges": [ { "from": 64810, "to": 64819 } ],
          "runtime": "14.0"
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
            procedure D_SkippedBySubscriber()
            begin
                Error('TRE4 FAIL: a test the subscriber skipped ran');
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
                if WorkDate() = 20300115D then
                    Error('TRE5 FAIL: next codeunit saw the WorkDate the previous one set [%1]', WorkDate());
            end;
        }

        codeunit 64813 "TRE Subscribers"
        {
            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Test Runner - Mgt", 'OnBeforeTestMethodRun', '', false, false)]
            local procedure SkipOne(CodeunitID: Integer; FunctionName: Text[128]; var CurrentTestMethodLine: Record "Test Method Line"; var Skip: Boolean)
            begin
                if (CodeunitID = 64811) and (FunctionName = 'D_SkippedBySubscriber') and (CurrentTestMethodLine."Test Codeunit" = 64811) then
                    Skip := true;
            end;

            [EventSubscriber(ObjectType::Codeunit, Codeunit::"Test Runner - Mgt", 'OnAfterTestMethodRun', '', false, false)]
            local procedure ReportOne(CodeunitID: Integer; CodeunitName: Text[30]; FunctionName: Text[128]; IsSuccess: Boolean; var CurrentTestMethodLine: Record "Test Method Line")
            begin
                if FunctionName = 'E_AfterEventCarriesTheOutcome' then
                    Error('TRE-AFTER %1|%2|%3|%4|%5', CodeunitID, CodeunitName, FunctionName, IsSuccess, CurrentTestMethodLine."Function");
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
        // A subscriber's Skip keeps the method from running.
        HasLine(output, "SKIP", "D_SkippedBySubscriber");
        // OnAfterTestMethodRun carries id, object name, AL name, outcome, and the line's Function.
        Assert.Contains("TRE-AFTER 64811|TRE Reset Tests|E_AfterEventCarriesTheOutcome|Yes|E_AfterEventCarriesTheOutcome",
            output, StringComparison.Ordinal);
        // 130453 on OnAfterCodeunitRun: WorkDate(CurrentWorkDate).
        HasLine(output, "PASS", "F_WorkDateIsPutBack");
        foreach (var marker in new[] { "TRE1 FAIL", "TRE2 FAIL", "TRE3 FAIL", "TRE4 FAIL", "TRE5 FAIL" })
            Lacks(output, marker);
    }

    [SkippableFact]
    public void TestRunnerNotLoaded_NothingIsRaised_AndTheRunIsUnaffected()
    {
        var dirs = RequireProvisioned();
        var bundle = WriteBundle("al-runner-test-runner-events-4813-absent", null, """
            codeunit 64811 "TRE Absent Tests"
            {
                Subtype = Test;

                trigger OnRun()
                begin
                    ApplicationArea('#Basic,#TREProbe');
                end;

                [Test]
                procedure A_AreaFromOnRunIsStillThere()
                begin
                    if ApplicationArea() <> '#Basic,#TREProbe' then
                        Error('TRE6 FAIL: without the Test Runner app the first test saw [%1]', ApplicationArea());
                end;
            }
            """);

        var (output, exit) = RunRunner(bundle, dirs);

        HasLine(output, "PASS", "A_AreaFromOnRunIsStillThere");
        Lacks(output, "TRE6 FAIL");
        Lacks(output, "Test Runner - Mgt");
        Assert.Equal(0, exit);
    }
}
