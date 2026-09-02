using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #2191: an unrelated asserterror stopped rolling back
/// every uncommitted write since #2170's fix for
/// TestTriggerRollback.OnDelete_Throws_RecordStillExists. Two shapes regressed —
/// RecordPatches.NoteTransactionWrite refreshed the per-table rollback snapshot to the
/// table's CURRENT live state on every write, so a table written twice since the last
/// commit point rolled back only to before the LAST write; and
/// RecordPatches.ForceDurableFailedInserts force-durabled every pending Insert() noted
/// during an asserterror-wrapped statement, including ones that had already landed and been
/// correctly rolled back.
///
/// The BEHAVIORAL claim ("an unrelated asserterror rolls back every write since the last
/// commit, regardless of how many separate writes landed or whether they ran before or
/// inside the asserterror'd statement") is a plain-BC-behaviour claim and belongs upstream —
/// see StefanMaron/BusinessCentral.AL.Language.Tests PR extending Codeunit 60943 "Test
/// AssertError Rollback" (three new cases), per
/// .claude/rules/bc-behavior-tests-go-upstream.md. This test exists so a regression in OUR
/// OWN rollback mechanism fails loudly here, spawning the real runner against a synthetic
/// bundle, without depending on the submodule pin having moved yet.
///
/// No Library Assert dependency (no "application" in the fixture's app.json — see
/// .claude/rules/no-base-app-in-csharp-tests.md): each test raises its own Error() with the
/// observed Count(), so the runner's own PASS/FAIL output is the assertion surface.
/// </summary>
public class AssertErrorRollbackScopeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
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
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void UnrelatedAssertError_RollsBackMultiWriteAndInStatementShapes()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-assert-error-rollback-2191", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b2191000-0000-4000-8000-000000002191",
          "name": "AssertErrorRollbackScope2191",
          "publisher": "Repro2191",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62191, "to": 62199 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "AersProbe.al"), """
        table 62191 "AERS Probe"
        {
            DataClassification = SystemMetadata;

            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; "Text Field"; Text[50]) { }
            }

            keys
            {
                key(PK; "Entry No.") { Clustered = true; }
            }
        }

        codeunit 62191 "AERS Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Initialize()
            var
                Probe: Record "AERS Probe";
            begin
                Probe.DeleteAll();
                Commit();
            end;

            // Regression shape 1 (AlRunner#2191): the same table written twice since the
            // last commit point must roll back BOTH writes on an unrelated asserterror, not
            // just the last one — NoteTransactionWrite refreshing the snapshot to CURRENT
            // live state on every write left only the last write's pre-image, so the first
            // Insert survived.
            [Test]
            procedure TwoInserts_SameTable_UnrelatedError_BothRollBack()
            var
                Probe: Record "AERS Probe";
            begin
                Initialize();
                Probe.Init();
                Probe."Entry No." := 1;
                Probe.Insert();
                Probe.Init();
                Probe."Entry No." := 2;
                Probe.Insert();

                asserterror Error('boom');

                if Probe.Count() <> 0 then
                    Error('ARM2 FAIL: expected Count()=0 after two uncommitted Inserts + unrelated error, got %1', Probe.Count());
            end;

            // Same regression shape, Insert-then-Modify instead of two Inserts.
            [Test]
            procedure InsertThenModify_UnrelatedError_RowRollsBack()
            var
                Probe: Record "AERS Probe";
            begin
                Initialize();
                Probe.Init();
                Probe."Entry No." := 1;
                Probe."Text Field" := 'ORIGINAL';
                Probe.Insert();
                Probe."Text Field" := 'CHANGED';
                Probe.Modify();

                asserterror Error('boom');

                if Probe.Count() <> 0 then
                    Error('ARM3 FAIL: expected Count()=0 after uncommitted Insert+Modify + unrelated error, got %1', Probe.Count());
            end;

            // Regression shape 2 (AlRunner#2191): writes made INSIDE the asserterror'd
            // statement, before a plain, unrelated Error(), must still roll back —
            // ForceDurableFailedInserts force-durabled every pending Insert() noted during
            // the statement regardless of whether it had already landed, re-adding rows the
            // rollback had correctly discarded.
            [Test]
            procedure ProcInsertsTwoThenErrors_AllRollsBack()
            var
                Probe: Record "AERS Probe";
            begin
                Initialize();

                asserterror InsertTwoThenError();

                if Probe.Count() <> 0 then
                    Error('ARM4 FAIL: expected Count()=0 after two inserts made inside the asserterror''d statement + unrelated error, got %1', Probe.Count());
            end;

            local procedure InsertTwoThenError()
            var
                Probe: Record "AERS Probe";
            begin
                Probe.Init();
                Probe."Entry No." := 1;
                Probe.Insert();
                Probe.Init();
                Probe."Entry No." := 2;
                Probe.Insert();
                Error('mid-run failure');
            end;

            // Must-not-regress control (TestTriggerRollback.al's shape): a write's OWN
            // trigger failing does not participate in this fix at all here (no OnInsert
            // trigger to fail), but a single uncommitted Insert followed by an unrelated
            // error must still roll back cleanly — the baseline case #2170/#2191 must both
            // keep passing.
            [Test]
            procedure SingleInsert_UnrelatedError_RollsBack()
            var
                Probe: Record "AERS Probe";
            begin
                Initialize();
                Probe.Init();
                Probe."Entry No." := 1;
                Probe.Insert();

                asserterror Error('boom');

                if Probe.Count() <> 0 then
                    Error('ARM1 FAIL: expected Count()=0 after a single uncommitted Insert + unrelated error, got %1', Probe.Count());
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all four rollback tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit62191.SingleInsert_UnrelatedError_RollsBack", output);
        Assert.Contains("PASS  Codeunit62191.TwoInserts_SameTable_UnrelatedError_BothRollBack", output);
        Assert.Contains("PASS  Codeunit62191.InsertThenModify_UnrelatedError_RowRollsBack", output);
        Assert.Contains("PASS  Codeunit62191.ProcInsertsTwoThenErrors_AllRollsBack", output);
    }
}
