using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #3449: <c>[CommitBehavior(CommitBehavior::Ignore)]</c> and
/// <c>[CommitBehavior(CommitBehavior::Error)]</c> had no effect, because the Cecil rewrite of
/// <c>ALDatabase.ALCommit</c> replaced BC's whole body — including its
/// <c>switch (session.CommitBehavior)</c> — with an unconditional commit point.
///
/// The BEHAVIOURAL claim ("Ignore makes Commit() a no-op so a later unrelated error still
/// rolls the write back; Error makes Commit() raise") is a plain-BC-behaviour claim and lives
/// upstream — StefanMaron/BusinessCentral.AL.Language.Tests#276, codeunit 60881
/// "Test Commit Behavior Attr", per .claude/rules/bc-behavior-tests-go-upstream.md. This test
/// exists so a regression in OUR OWN commit-point bookkeeping fails loudly here, spawning the
/// real runner against a synthetic bundle, without depending on the submodule pin having moved
/// yet (that corpus PR had not merged when this test was written).
///
/// The AL assertions below are all plain BC-behaviour claims already green upstream; what
/// they buy is coverage while the pin has not moved, which is a temporary justification and
/// not a claim that the corpus cannot see them. The Lang-resource half of that question —
/// whether the Error branch really resolved BC's resource or fell back — is NOT decidable
/// from AL, because the fallback string is byte-identical to the resource; it is settled
/// in-process by CommitProhibitedMessageTests instead.
///
/// No Library Assert / Base Application dependency (no "application" in the fixture's
/// app.json — see .claude/rules/no-base-app-in-csharp-tests.md): each test raises its own
/// Error() carrying the observed state, and the runner's own PASS/FAIL output is the
/// assertion surface.
/// </summary>
public class CommitBehaviorAttributeTests
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
    public void CommitBehaviorAttribute_IsConsultedByALCommit()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-commit-behavior-3449");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b3449000-0000-4000-8000-000000003449",
          "name": "CommitBehavior3449",
          "publisher": "Repro3449",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62449, "to": 62459 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "CbhProbe.al"), """
        table 62449 "CBH Probe"
        {
            DataClassification = SystemMetadata;

            fields
            {
                field(1; "Entry No."; Integer) { }
            }

            keys
            {
                key(PK; "Entry No.") { Clustered = true; }
            }
        }

        codeunit 62450 "CBH Helpers"
        {
            internal procedure InsertCommitThenError(EntryNo: Integer)
            var
                Probe: Record "CBH Probe";
            begin
                Probe."Entry No." := EntryNo;
                Probe.Insert();
                Commit();
                Error('boom');
            end;

            [CommitBehavior(CommitBehavior::Ignore)]
            internal procedure InsertCommitThenErrorIgnored(EntryNo: Integer)
            var
                Probe: Record "CBH Probe";
            begin
                Probe."Entry No." := EntryNo;
                Probe.Insert();
                Commit();
                Error('boom');
            end;

            [CommitBehavior(CommitBehavior::Ignore)]
            internal procedure CallUnattributedHelperIgnored(EntryNo: Integer)
            begin
                InsertCommitThenError(EntryNo);
            end;

            [CommitBehavior(CommitBehavior::Error)]
            internal procedure CommitUnderErrorBehavior(EntryNo: Integer)
            var
                Probe: Record "CBH Probe";
            begin
                Probe."Entry No." := EntryNo;
                Probe.Insert();
                Commit();
            end;
        }

        codeunit 62449 "CBH Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            var
                Helpers: Codeunit "CBH Helpers";

            local procedure Initialize()
            var
                Probe: Record "CBH Probe";
            begin
                Probe.DeleteAll();
                Commit();
            end;

            // Control: the same helper body with NO attribute. If this one ever stops
            // committing, the tests below would pass for the wrong reason — they would be
            // measuring a broken Commit() rather than an honored attribute.
            [Test]
            procedure Unattributed_CommitBeforeErrorIsDurable()
            var
                Probe: Record "CBH Probe";
            begin
                Initialize();
                asserterror Helpers.InsertCommitThenError(1);
                if not Probe.Get(1) then
                    Error('CBH1 FAIL: without the attribute the Commit() must be an ordinary commit and the row must survive the error');
            end;

            // Regression shape (AlRunner#3449): the attribute must make Commit() a no-op, so
            // the rollback boundary does not move and the unrelated Error() undoes the Insert.
            [Test]
            procedure Ignore_CommitBeforeErrorIsNotDurable()
            var
                Probe: Record "CBH Probe";
            begin
                Initialize();
                asserterror Helpers.InsertCommitThenErrorIgnored(2);
                if Probe.Get(2) then
                    Error('CBH2 FAIL: under CommitBehavior::Ignore the Commit() must do nothing, so the error must roll the Insert back');
                if Probe.Count() <> 0 then
                    Error('CBH2 FAIL: expected Count()=0, got %1', Probe.Count());
            end;

            // The attribute governs the whole DYNAMIC scope: the Commit() here is issued by an
            // unattributed callee one frame down.
            [Test]
            procedure Ignore_GovernsUnattributedCallee()
            var
                Probe: Record "CBH Probe";
            begin
                Initialize();
                asserterror Helpers.CallUnattributedHelperIgnored(3);
                if Probe.Get(3) then
                    Error('CBH3 FAIL: CommitBehavior::Ignore must govern a Commit() issued by an unattributed callee too');
            end;

            // The scope is popped on return: after the attributed call, an ordinary Commit()
            // must commit again. Without this the fix could "work" by disabling Commit() for
            // the rest of the session.
            [Test]
            procedure Ignore_ScopeIsPoppedOnReturn()
            var
                Probe: Record "CBH Probe";
            begin
                Initialize();
                asserterror Helpers.InsertCommitThenErrorIgnored(4);

                Probe."Entry No." := 5;
                Probe.Insert();
                Commit();
                asserterror Error('unrelated');

                Clear(Probe);
                if not Probe.Get(5) then
                    Error('CBH4 FAIL: the Ignore scope must end when the attributed call returns — the later ordinary Commit() must commit');
            end;

            // CommitBehavior::Error must raise, and must raise BC's OWN text: the runner builds
            // it by reflection out of Lang.CommitProhibited and would otherwise fall back to a
            // hardcoded string with nothing pointing that out.
            [Test]
            procedure ErrorBehavior_CommitRaisesBcOwnText()
            var
                Probe: Record "CBH Probe";
                ErrText: Text;
            begin
                Initialize();
                asserterror Helpers.CommitUnderErrorBehavior(6);
                ErrText := GetLastErrorText();

                if StrPos(ErrText, 'Commit is prohibited in the current scope') = 0 then
                    Error('CBH5 FAIL: expected BC''s own Lang.CommitProhibited text, got %1', ErrText);
                if Probe.Get(6) then
                    Error('CBH5 FAIL: the Insert before the prohibited Commit() must roll back');
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 0,
            $"Expected all five tests to pass (exit 0); got exit {exitCode}.\n{output}");
        Assert.DoesNotContain("FAIL", output);
        Assert.Contains("PASS  Codeunit62449.Unattributed_CommitBeforeErrorIsDurable", output);
        Assert.Contains("PASS  Codeunit62449.Ignore_CommitBeforeErrorIsNotDurable", output);
        Assert.Contains("PASS  Codeunit62449.Ignore_GovernsUnattributedCallee", output);
        Assert.Contains("PASS  Codeunit62449.Ignore_ScopeIsPoppedOnReturn", output);
        Assert.Contains("PASS  Codeunit62449.ErrorBehavior_CommitRaisesBcOwnText", output);
    }
}
