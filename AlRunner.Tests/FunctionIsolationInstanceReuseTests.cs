using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4826: `--isolation test` is AL's <c>TestIsolation = Function</c>, and on BC that runs every
/// [Test] of a codeunit on ONE instance, runs OnRun once, and rolls the database back after each
/// test to the state OnRun left. The fixture is the corpus probe's (corpus PR #517,
/// tests/al-language-isolation-probe), minus its two TestRunner codeunits; every one of its seven
/// tests passed under the Function runner on BC 28.4.53241.55454 (Windows nightly run
/// 36727084058), and only T5 failed under the Codeunit control.
///
/// No Base Application dependency: each AL test raises its own Error() carrying the observed
/// value, and the runner's FAIL lines are the assertion surface.
/// </summary>
public class FunctionIsolationInstanceReuseTests
{
    private const int FixtureId = 64822;

    private static readonly string[] Tests =
    {
        "T1_SetsGlobalCounterSingleInstanceAndRow",
        "T2_GlobalSetByT1IsStillSet",
        "T3_CallCounterCountsEveryEarlierTest",
        "T4_SingleInstanceSetByT1IsStillSet",
        "T5_RowInsertedByT1IsRolledBack",
        "T6_RowInsertedByOnRunIsStillThere",
        "T7_OnRunRanOnceOnThisInstance",
        "LeakA_DoesNotSeeLeakB",
        "LeakB_DoesNotSeeLeakA",
    };

    private static string WriteBundle(string name)
    {
        var root = TestScratch.Dir(name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b4826000-0000-4000-8000-000000004826",
          "name": "FunctionIsolationProbe4826",
          "publisher": "Repro4826",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 64820, "to": 64826 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Probe.al"), """
        table 64820 "FIR Probe Row"
        {
            DataClassification = SystemMetadata;
            fields { field(1; "Key"; Code[20]) { } }
            keys { key(PK; "Key") { Clustered = true; } }
        }

        codeunit 64821 "FIR Single Instance"
        {
            SingleInstance = true;
            var
                Mark: Integer;
            procedure SetMark(NewMark: Integer) begin Mark := NewMark; end;
            procedure GetMark(): Integer begin exit(Mark); end;
        }

        codeunit 64822 "FIR Fixture"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            var
                GlobalMark: Integer;
                CallCount: Integer;
                OnRunCount: Integer;

            trigger OnRun()
            var
                ProbeRow: Record "FIR Probe Row";
            begin
                OnRunCount += 1;
                if not ProbeRow.Get('ONRUN') then begin
                    ProbeRow."Key" := 'ONRUN';
                    ProbeRow.Insert();
                end;
            end;

            [Test]
            procedure T1_SetsGlobalCounterSingleInstanceAndRow()
            var
                ProbeRow: Record "FIR Probe Row";
                ProbeSingleInstance: Codeunit "FIR Single Instance";
            begin
                CallCount += 1;
                GlobalMark := 4826;
                ProbeSingleInstance.SetMark(4826);
                if ProbeRow.Get('MARK') then
                    ProbeRow.Delete();
                ProbeRow."Key" := 'MARK';
                ProbeRow.Insert();
                ExpectInteger(1, CallCount, 'T1 first on its instance');
            end;

            [Test]
            procedure T2_GlobalSetByT1IsStillSet()
            begin
                CallCount += 1;
                ExpectInteger(4826, GlobalMark, 'FIR2 global');
            end;

            [Test]
            procedure T3_CallCounterCountsEveryEarlierTest()
            begin
                CallCount += 1;
                ExpectInteger(3, CallCount, 'FIR3 call counter');
            end;

            [Test]
            procedure T4_SingleInstanceSetByT1IsStillSet()
            var
                ProbeSingleInstance: Codeunit "FIR Single Instance";
            begin
                CallCount += 1;
                ExpectInteger(4826, ProbeSingleInstance.GetMark(), 'FIR4 SingleInstance mark');
            end;

            [Test]
            procedure T5_RowInsertedByT1IsRolledBack()
            var
                ProbeRow: Record "FIR Probe Row";
            begin
                CallCount += 1;
                if ProbeRow.Get('MARK') then
                    Error('FIR5 the row T1 inserted is still there');
            end;

            [Test]
            procedure T6_RowInsertedByOnRunIsStillThere()
            var
                ProbeRow: Record "FIR Probe Row";
            begin
                CallCount += 1;
                if not ProbeRow.Get('ONRUN') then
                    Error('FIR6 the row OnRun inserted is gone');
            end;

            [Test]
            procedure T7_OnRunRanOnceOnThisInstance()
            begin
                CallCount += 1;
                ExpectInteger(1, OnRunCount, 'FIR7 OnRun executions on this instance');
            end;

            local procedure ExpectInteger(Expected: Integer; Actual: Integer; What: Text)
            begin
                if Actual <> Expected then
                    Error('%1: expected %2, observed %3', What, Expected, Actual);
            end;
        }

        // Two codeunits with an OnRun whose tests each leave a row behind. Whichever runs second
        // must not see the other's row: under Test isolation the store is reset BEFORE OnRun, so
        // the post-OnRun snapshot cannot carry the previous codeunit's last test into this one.
        codeunit 64823 "FIR Leak A"
        {
            Subtype = Test;
            TestPermissions = Disabled;
            trigger OnRun() begin end;

            [Test]
            procedure LeakA_DoesNotSeeLeakB()
            var
                ProbeRow: Record "FIR Probe Row";
            begin
                if ProbeRow.Get('LEAK-B') then
                    Error('FIRL codeunit B''s row reached codeunit A');
                ProbeRow."Key" := 'LEAK-A';
                ProbeRow.Insert();
            end;
        }

        codeunit 64824 "FIR Leak B"
        {
            Subtype = Test;
            TestPermissions = Disabled;
            trigger OnRun() begin end;

            [Test]
            procedure LeakB_DoesNotSeeLeakA()
            var
                ProbeRow: Record "FIR Probe Row";
            begin
                if ProbeRow.Get('LEAK-A') then
                    Error('FIRL codeunit A''s row reached codeunit B');
                ProbeRow."Key" := 'LEAK-B';
                ProbeRow.Insert();
            end;
        }
        """);
        return root;
    }

    private static void AssertRan(ServerRunResult result)
    {
        foreach (var t in Tests)
            Assert.True(result.Tests.Any(r => r.Name.EndsWith("." + t, StringComparison.Ordinal)), $"{t} did not run:\n{result}");
    }

    [SkippableFact]
    public async Task TestIsolation_RunsEveryTestOnOneInstance_AndRollsBackToThePostOnRunState()
    {
        TestArtifacts.SkipIfMissing();
        var result = await SuiteServer.RunViaServer(
            new[] { WriteBundle("al-runner-function-isolation-4826-test") }, Array.Empty<string>(), testIsolation: "test");

        AssertRan(result);
        Assert.True(result.Tests.All(t => t.Status == "pass") && result.CompilationErrors.Count == 0,
            $"every probe test passed on BC under TestIsolation = Function (run 36727084058):\n{result}");
        Assert.Equal(0, result.ExitCode);
    }

    [SkippableFact]
    public async Task CodeunitIsolation_FailsOnlyT5_TheRowIsNotRolledBackBetweenTests()
    {
        TestArtifacts.SkipIfMissing();
        var result = await SuiteServer.RunViaServer(
            new[] { WriteBundle("al-runner-function-isolation-4826-codeunit") }, Array.Empty<string>(), testIsolation: "codeunit");

        AssertRan(result);
        var failures = result.Tests.Where(t => t.Status != "pass").ToList();
        Assert.True(failures.Count == 1 && failures[0].Name == $"Codeunit{FixtureId}.T5_RowInsertedByT1IsRolledBack",
            $"expected exactly T5 to fail under Codeunit isolation, as on BC:\n{result}");
        Assert.Contains("FIR5 the row T1 inserted is still there", failures[0].Message);
    }
}
