// TestPageCurrFieldNoDispatchTests — issue #2705.
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that OUR OWN
// wiring — LiveNavTestField.Value's setter in MockTestPage.cs — stamps NavRecord.CurrFieldNo to
// the bound field's own number before calling ALValidateAsync, and restores the previous value
// in a finally block afterward, for a TestPage SetValue on both the table's own field and a
// tableextension field. Also proves Rec.Validate from AL code does NOT stamp CurrFieldNo, and
// that the stamped value does not outlive the single Validate call (a later OnModify sees 0
// again, not the field number the last SetValue stamped).
//
// The BEHAVIORAL claim (what real BC does on each of these surfaces) is the corpus's to
// adjudicate — see StefanMaron/BusinessCentral.AL.Language.Tests, per
// .claude/rules/bc-behavior-tests-go-upstream.md. This test exists so a regression in OUR OWN
// dispatch mechanism fails loudly here, in seconds, without needing the corpus's al-language
// submodule pin bumped first.
//
// RED/GREEN proof: before LiveNavTestField.Value's setter set NavRecord.CurrFieldNo, arms A and
// C below failed with "Assert.AreEqual failed. Expected:<10> ... Actual:<0>" (and the
// tableextension field's own field number for arm C) — the table field's OnValidate always
// read 0, whether the write came from a page or from code. Arms B and D already passed before
// the fix and must keep passing after it: only the page-write path stamps CurrFieldNo, and only
// for the duration of that one validate call.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageCurrFieldNoDispatchTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageCurrFieldNoDispatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-currfieldno-dispatch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string[] ExtraPackageCacheArgs()
    {
        var platformApps = TestArtifacts.PlatformAppsDir();
        return Directory.Exists(platformApps)
            ? new[] { "--package-cache", platformApps }
            : Array.Empty<string>();
    }

    private void WriteBundle()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "e2f3a4b5-6c7d-4890-9a1b-6c7d8e9f0c3e",
          "name": "Runner Mechanism - TestPage CurrFieldNo Dispatch",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62470, "to": 62479 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "CfnRow.Table.al"), """
        table 62470 "Cfn Row"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; PK; Code[10]) { }
                field(2; Amount; Decimal)
                {
                    trigger OnValidate()
                    begin
                        Rec.ValidateFieldNo := CurrFieldNo;
                    end;
                }
                field(3; ValidateFieldNo; Integer) { }
                field(4; ModifyFieldNo; Integer) { }
            }

            keys
            {
                key(K; PK) { Clustered = true; }
            }

            trigger OnModify()
            begin
                Rec.ModifyFieldNo := CurrFieldNo;
            end;
        }
        """);

        File.WriteAllText(Path.Combine(_root, "CfnExt.TableExt.al"), """
        tableextension 62471 "Cfn Row Ext" extends "Cfn Row"
        {
            fields
            {
                field(50000; "Ext Amount"; Decimal)
                {
                    trigger OnValidate()
                    begin
                        Rec."Ext ValidateFieldNo" := CurrFieldNo;
                    end;
                }
                field(50001; "Ext ValidateFieldNo"; Integer) { }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "CfnCard.Page.al"), """
        page 62472 "Cfn Card"
        {
            PageType = Card;
            SourceTable = "Cfn Row";
            ApplicationArea = All;
            UsageCategory = Administration;

            layout
            {
                area(Content)
                {
                    field(PK; Rec.PK) { ApplicationArea = All; }
                    field(RecAmount; Rec.Amount) { ApplicationArea = All; }
                    field(RecValidateFieldNo; Rec.ValidateFieldNo) { ApplicationArea = All; }
                    field(RecExtAmount; Rec."Ext Amount") { ApplicationArea = All; }
                    field(RecExtValidateFieldNo; Rec."Ext ValidateFieldNo") { ApplicationArea = All; }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "CfnTests.Codeunit.al"), """
        codeunit 62473 "Cfn Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Seed(PK: Code[10])
            var
                Row: Record "Cfn Row";
            begin
                if Row.Get(PK) then
                    Row.Delete();
                Row.Init();
                Row.PK := PK;
                Row.Insert();
            end;

            [Test]
            procedure SetValue_OwnTableField_OnValidateSeesCurrFieldNo()
            var
                Row: Record "Cfn Row";
                Card: TestPage "Cfn Card";
            begin
                Seed('A');

                Card.OpenEdit();
                Card.First();
                Card.RecAmount.SetValue(50);
                if Card.RecValidateFieldNo.AsInteger() <> Row.FieldNo(Amount) then
                    Error('expected CurrFieldNo = %1, got %2', Row.FieldNo(Amount), Card.RecValidateFieldNo.AsInteger());
                Card.Close();
            end;

            [Test]
            procedure Validate_FromCode_OnValidateSeesZero()
            var
                Row: Record "Cfn Row";
            begin
                Seed('B');
                Row.Get('B');
                Row.Validate(Amount, 50);
                if Row.ValidateFieldNo <> 0 then
                    Error('expected CurrFieldNo = 0 on Rec.Validate from code, got %1', Row.ValidateFieldNo);
            end;

            [Test]
            procedure SetValue_TableExtensionField_OnValidateSeesCurrFieldNo()
            var
                Row: Record "Cfn Row";
                Card: TestPage "Cfn Card";
            begin
                Seed('C');

                Card.OpenEdit();
                Card.First();
                Card.RecExtAmount.SetValue(50);
                if Card.RecExtValidateFieldNo.AsInteger() <> Row.FieldNo("Ext Amount") then
                    Error('expected CurrFieldNo = %1, got %2', Row.FieldNo("Ext Amount"), Card.RecExtValidateFieldNo.AsInteger());
                Card.Close();
            end;

            [Test]
            procedure SetValue_ThenClose_OnModifySeesZeroNotTheStampedField()
            var
                Row: Record "Cfn Row";
                Card: TestPage "Cfn Card";
            begin
                Seed('D');

                Card.OpenEdit();
                Card.First();
                Card.RecAmount.SetValue(50);
                Card.Close();

                Row.Get('D');
                if Row.ModifyFieldNo <> 0 then
                    Error('expected OnModify to see CurrFieldNo = 0, got %1 (must not outlive the earlier Validate)', Row.ModifyFieldNo);
            end;
        }
        """);
    }

    private (string output, int exit) RunBundled()
    {
        var args = new StringBuilder(
            TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + $" \"{_root}\"");
        foreach (var a in ExtraPackageCacheArgs()) args.Append($" \"{a}\"");
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

    /// <summary>
    /// Positive (SetValue on both the table's own field and a tableextension field stamps
    /// CurrFieldNo to that field's own number) and negative (Rec.Validate from code sees 0, and
    /// the stamp does not outlive the Validate call it was set for) in one bundle — the shape
    /// #2705 depends on the runner getting all four right simultaneously.
    /// </summary>
    [SkippableFact]
    public void CurrFieldNo_StampedOnlyForTestPageSetValueDuration()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62473.SetValue_OwnTableField_OnValidateSeesCurrFieldNo", output);
        Assert.Contains("PASS  Codeunit62473.Validate_FromCode_OnValidateSeesZero", output);
        Assert.Contains("PASS  Codeunit62473.SetValue_TableExtensionField_OnValidateSeesCurrFieldNo", output);
        Assert.Contains("PASS  Codeunit62473.SetValue_ThenClose_OnModifySeesZeroNotTheStampedField", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
