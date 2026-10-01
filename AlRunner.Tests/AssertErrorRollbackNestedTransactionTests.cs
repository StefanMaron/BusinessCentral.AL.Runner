using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #2413: a write made before a plain nested BC transaction
/// (Query.Open()'s RunOnBeforeOpenTriggerAsync — Session.BeginTransaction() /
/// Session.EndTransaction(commit: true)) survived a LATER, unrelated asserterror, because
/// AlRunner.Infrastructure.NclCecilRewrite ("8g", AlRunner#1946) prepended
/// RecordPatches.NoteTransactionEnd to BOTH SessionTransactionExtensions.EndTransaction and
/// .EndTransactionWorldAndTransaction, and NoteTransactionEnd calls MarkCommitPoint()
/// whenever commit is true. That is right only for a completed transaction WORLD (a guarded
/// Codeunit.Run, or `Ok := XmlPort.Import(...)`) — real BC's plain EndTransaction only pops a
/// nested level of the CALLER's already-open transaction, and does not commit anything.
///
/// The BEHAVIORAL claim ("Query.Open()/statement-form XmlPort.Import() between a write and a
/// later unrelated asserterror do not make that write durable") is a plain-BC-behaviour claim
/// and belongs upstream — see StefanMaron/BusinessCentral.AL.Language.Tests PR extending
/// error-handling/ with Codeunit 60945 "Test AssertError Rollback NTx", per
/// .claude/rules/bc-behavior-tests-go-upstream.md. This test exists so a regression in OUR
/// OWN commit-point bookkeeping fails loudly here, running the real runner (SuiteServer) against a
/// synthetic bundle, without depending on the submodule pin having moved yet (the corpus PR
/// had not merged when this test was written).
///
/// No Library Assert / Base Application dependency (no "application" in the fixture's
/// app.json — see .claude/rules/no-base-app-in-csharp-tests.md): Query is a platform-level
/// object type, so each test raises its own Error() with the observed state and the runner's
/// own PASS/FAIL output is the assertion surface.
/// </summary>
public class AssertErrorRollbackNestedTransactionTests
{
    [SkippableFact]
    public async Task UnrelatedAssertError_RollsBackWritesMadeBeforePlainNestedTransaction()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-assert-error-rollback-ntx-2413");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b2413000-0000-4000-8000-000000002413",
          "name": "AssertErrorRollbackNestedTx2413",
          "publisher": "Repro2413",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62413, "to": 62419 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "AentProbe.al"), """
        table 62413 "AENT Probe"
        {
            DataClassification = SystemMetadata;

            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; "Integer Field"; Integer) { }
            }

            keys
            {
                key(PK; "Entry No.") { Clustered = true; }
            }
        }

        query 62413 "AENT Probe Query"
        {
            elements
            {
                dataitem(Row; "AENT Probe")
                {
                    column(EntryNo; "Entry No.") { }
                    column(IntegerValue; "Integer Field") { }
                }
            }
        }

        codeunit 62413 "AENT Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Initialize()
            var
                Probe: Record "AENT Probe";
            begin
                Probe.DeleteAll();
                Commit();
            end;

            // Regression shape (AlRunner#2413): a committed row, then an uncommitted Modify,
            // then Query.Open()/Read()/Close() — a plain nested transaction that must NOT
            // become a commit point — then a later, unrelated asserterror. The Modify must
            // still roll back.
            [Test]
            procedure Modify_QueryOpen_UnrelatedError_ModifyRollsBack()
            var
                Probe: Record "AENT Probe";
                ProbeQuery: Query "AENT Probe Query";
            begin
                Initialize();
                Probe.Init();
                Probe."Entry No." := 1;
                Probe."Integer Field" := 10;
                Probe.Insert();
                Commit();

                Probe.Get(1);
                Probe."Integer Field" := 99;
                Probe.Modify();

                ProbeQuery.Open();
                ProbeQuery.Read();
                ProbeQuery.Close();

                asserterror Error('boom');

                Clear(Probe);
                Probe.Get(1);
                if Probe."Integer Field" <> 10 then
                    Error('AENT1 FAIL: expected Integer Field=10 after Query.Open() + unrelated error rolled back the uncommitted Modify, got %1', Probe."Integer Field");
            end;

            // Same regression shape with an uncommitted Insert instead of a Modify.
            [Test]
            procedure Insert_QueryOpen_UnrelatedError_InsertRollsBack()
            var
                Probe: Record "AENT Probe";
                ProbeQuery: Query "AENT Probe Query";
            begin
                Initialize();
                Probe.Init();
                Probe."Entry No." := 7;
                Probe.Insert();

                ProbeQuery.Open();
                ProbeQuery.Read();
                ProbeQuery.Close();

                asserterror Error('boom');

                if Probe.Count() <> 0 then
                    Error('AENT2 FAIL: expected Count()=0 after Query.Open() + unrelated error rolled back the uncommitted Insert, got %1', Probe.Count());
            end;

            // Must-not-regress control: Query.Open() with NO pending write at all must not
            // itself throw or otherwise disrupt an unrelated asserterror afterwards.
            [Test]
            procedure QueryOpen_NoWrite_UnrelatedError_NoThrow()
            var
                ProbeQuery: Query "AENT Probe Query";
            begin
                Initialize();

                ProbeQuery.Open();
                ProbeQuery.Close();

                asserterror Error('boom');
            end;
        }
        """);

        var r = await SuiteServer.RunViaServer(root);

        Assert.True(r.ExitCode == 0,
            $"Expected all three tests to pass (exit 0); got exit {r.ExitCode}.\n{r}");
        r.AssertNoFailures();
        r.AssertPassed("Codeunit62413.Modify_QueryOpen_UnrelatedError_ModifyRollsBack");
        r.AssertPassed("Codeunit62413.Insert_QueryOpen_UnrelatedError_InsertRollsBack");
        r.AssertPassed("Codeunit62413.QueryOpen_NoWrite_UnrelatedError_NoThrow");
    }
}
