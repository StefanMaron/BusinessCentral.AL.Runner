using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #2549: <c>TestPage.Field.Lookup()</c> refused
/// <c>testpage-lookup</c> for any field whose PAGE CONTROL declared no OnLookup, even when the
/// field's own TABLE declared one.
///
/// <para>AL spells OnLookup two unrelated ways — <c>trigger OnLookup(var Text: Text): Boolean</c>
/// on a page control, and a parameterless <c>trigger OnLookup()</c> on a table field that writes
/// into <c>Rec</c> itself. Real BC tries the control first, then the table field, then the
/// TableRelation lookup UI. <c>RunnerPageInstance.RaiseOnLookup</c> implemented only the first
/// and refused as soon as it missed, so a field whose table carries the lookup was refused as if
/// it had no lookup at all — even though the handler was already wired onto the metafield by
/// <c>RecordPatches.NclMetaTableBuilder</c> and nothing ever dispatched it.</para>
///
/// <para>Only the third case is genuinely out of scope: a field with neither trigger gets its
/// lookup from a TableRelation, which on real BC opens the related table's list page. That one
/// must keep refusing, and the last test here is what stops a fix for the first two from
/// quietly turning the refusal into a no-op — which is the failure mode
/// <c>.claude/rules/loud-failures.md</c> exists for.</para>
///
/// <para>The BC-behaviour half of this claim — that BC runs the table field's trigger, and that
/// a control trigger wins over it — is adjudicated upstream in the al-language corpus per
/// <c>.claude/rules/bc-behavior-tests-go-upstream.md</c>. This test exists so a regression in
/// the runner's own dispatch fails here too, without waiting on the submodule pin.</para>
///
/// <para>No <c>"application"</c> in the fixture manifest (see
/// <c>.claude/rules/no-base-app-in-csharp-tests.md</c>): each AL test raises its own Error() with
/// the value it observed, so the runner's PASS/FAIL output is the assertion surface.</para>
/// </summary>
public class TestPageFieldTableLookupTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
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
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static string WriteBundle()
    {
        var root = TestScratch.Dir("al-runner-testpage-field-table-lookup-2549");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c2549000-0000-4000-8000-000000002549",
          "name": "TestPageFieldTableLookup2549",
          "publisher": "Repro2549",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62560, "to": 62569 } ],
          "runtime": "14.0"
        }
        """);

        // Three fields, one per case the dispatch has to tell apart. Each trigger writes a
        // value that names WHERE it ran, so a failure reports which handler fired rather than
        // only that the wrong one did.
        File.WriteAllText(Path.Combine(root, "TftlRow.Table.al"), """
        table 62560 "Tftl Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "No."; Code[20]) { }
                // Table trigger only: the page control over this field declares nothing.
                field(2; "Table Only"; Code[20])
                {
                    trigger OnLookup()
                    begin
                        "Table Only" := 'FROM-TABLE';
                    end;
                }
                // BOTH: the control also declares one, and the control must win.
                field(3; Both; Code[20])
                {
                    trigger OnLookup()
                    begin
                        Both := 'FROM-TABLE';
                    end;
                }
                // Neither: the lookup would come from the TableRelation, i.e. from a list page
                // the runner cannot stand up. Must keep refusing.
                field(4; "Relation Only"; Code[20])
                {
                    TableRelation = "Tftl Row"."No.";
                }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }
        """);

        File.WriteAllText(Path.Combine(root, "TftlCard.Page.al"), """
        page 62561 "Tftl Card"
        {
            PageType = Card;
            SourceTable = "Tftl Row";
            ApplicationArea = All;
            UsageCategory = None;

            layout
            {
                area(Content)
                {
                    field("No."; Rec."No.") { ApplicationArea = All; }
                    field("Table Only"; Rec."Table Only") { ApplicationArea = All; }
                    field(Both; Rec.Both)
                    {
                        ApplicationArea = All;
                        trigger OnLookup(var Text: Text): Boolean
                        begin
                            Text := 'FROM-CONTROL';
                            exit(true);
                        end;
                    }
                    field("Relation Only"; Rec."Relation Only") { ApplicationArea = All; }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(root, "TftlTests.Codeunit.al"), """
        codeunit 62562 "Tftl Tests"
        {
            Subtype = Test;

            local procedure OpenOn(var Card: TestPage "Tftl Card")
            var
                Row: Record "Tftl Row";
            begin
                Row.DeleteAll();
                Row.Init();
                Row."No." := 'R1';
                Row.Insert();
                Card.OpenEdit();
                Card.GoToRecord(Row);
            end;

            [Test]
            procedure TableFieldOnLookupRunsWhenTheControlDeclaresNone()
            var
                Card: TestPage "Tftl Card";
            begin
                OpenOn(Card);
                Card."Table Only".Lookup();
                if Card."Table Only".Value <> 'FROM-TABLE' then
                    Error('table field OnLookup did not run: field reads %1', Card."Table Only".Value);
                Card.Close();
            end;

            [Test]
            procedure ControlOnLookupWinsOverTheTableFieldTrigger()
            var
                Card: TestPage "Tftl Card";
            begin
                OpenOn(Card);
                Card.Both.Lookup();
                if Card.Both.Value <> 'FROM-CONTROL' then
                    Error('the control trigger must win over the table field trigger, field reads %1', Card.Both.Value);
                Card.Close();
            end;

            [Test]
            procedure FieldWithNeitherTriggerStillRefuses()
            var
                Card: TestPage "Tftl Card";
            begin
                OpenOn(Card);
                asserterror Card."Relation Only".Lookup();
                if StrPos(GetLastErrorText(), 'testpage-lookup') = 0 then
                    Error('a TableRelation-only lookup must still refuse by name, got: %1', GetLastErrorText());
                Card.Close();
            end;
        }
        """);

        return root;
    }

    /// <summary>
    /// All three cases on one bundle. Splitting them would let a fix that dispatches the table
    /// trigger for EVERY field — turning the third case's loud refusal into a silent no-op —
    /// pass two tests out of three and look like progress.
    /// </summary>
    [SkippableFact]
    public void FieldLookup_FallsBackToTheTableFieldTrigger_ButOnlyWhenOneExists()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(WriteBundle());

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62562.TableFieldOnLookupRunsWhenTheControlDeclaresNone", output);
        Assert.Contains("PASS  Codeunit62562.ControlOnLookupWinsOverTheTableFieldTrigger", output);
        Assert.Contains("PASS  Codeunit62562.FieldWithNeitherTriggerStillRefuses", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
