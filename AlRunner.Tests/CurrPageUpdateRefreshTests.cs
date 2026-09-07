// CurrPageUpdateRefreshTests — issue #3373.
//
// This is a RUNNER-MECHANISM test. The BEHAVIORAL claim ("real BC raises the page's
// OnAfterGetCurrRecord after the trigger that called CurrPage.Update returns") is proven
// upstream, against a live service tier, by codeunit 60496 "ALT Page Update Test" in
// StefanMaron/BusinessCentral.AL.Language.Tests — per
// .claude/rules/bc-behavior-tests-go-upstream.md. It lives here as well, in C#, because the
// corpus pin cannot be bumped until that PR merges, and because what this pins is one specific
// piece of OUR wiring: RunnerPageInstance subscribing to NavForm's public UpdateRequest event
// in the absent client's place, and realising it at the outermost AL trigger's return rather
// than at the event.
//
// Three arms drive the same bundle in one run, and the pair is what carries the claim: the
// control page differs from the Update page in exactly one respect, the absence of the
// CurrPage.Update call. An implementation that refreshed after every SetValue would pass the
// first arm and fail the second, and one that refreshed at the event rather than at the
// trigger's return would fail the ORDER assertion while still passing a bare "the value
// changed" check — which is why the trace is asserted as an exact string.
//
// The fixture declares no "application", per .claude/rules/no-base-app-in-csharp-tests.md.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class CurrPageUpdateRefreshTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public CurrPageUpdateRefreshTests()
    {
        _root = TestScratch.Dir("al-runner-currpage-update-3373");
        Directory.CreateDirectory(_root);
        WriteBundle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void CurrPageUpdate_RefreshesTheHostAfterTheTriggerReturns_AndOnlyWhenItIsCalled()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        // Each arm is a [Test] procedure asserting inside AL, so a green run IS the claim.
        // The exit code alone would not distinguish "passed" from "discovered nothing", hence
        // the explicit pass/fail counts below.
        Assert.True(output.Contains("pass:        4"),
            $"expected all four arms to pass; exit={exit}\n{output}");
        Assert.DoesNotContain("fail:        1", output);
        Assert.DoesNotContain("fail:        2", output);
        Assert.DoesNotContain("fail:        3", output);
        Assert.DoesNotContain("fail:        4", output);
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            {
              "id": "b7c1e3a2-4d59-4f80-9a12-77e6c0f4a331",
              "name": "CurrPage Update Refresh Fixture",
              "publisher": "AL Runner Tests",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 90350, "to": 90369 } ],
              "platform": "27.0.0.0",
              "runtime": "15.0",
              "target": "Cloud"
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Row.Table.al"), """
            table 90350 "CPU Row"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; Amount; Integer) { }
                }
                keys { key(PK; "No.") { Clustered = true; } }
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Trace.Codeunit.al"), """
            codeunit 90351 "CPU Trace"
            {
                SingleInstance = true;
                var
                    Order: Text;
                procedure Reset() begin Order := ''; end;
                procedure Note(Tag: Text) begin Order += Tag + ';'; end;
                procedure Get(): Text begin exit(Order); end;
            }
            """);

        // The page global the FactBox column reads is written ONLY from the host's
        // OnAfterGetCurrRecord, and the part has no SubPageLink, so nothing else can move it.
        File.WriteAllText(Path.Combine(_root, "Facts.Page.al"), """
            page 90352 "CPU Facts"
            {
                PageType = ListPart;
                SourceTable = Integer;
                SourceTableTemporary = true;
                Editable = false;
                layout
                {
                    area(Content)
                    {
                        repeater(Rows)
                        {
                            field(Derived; Derived[Rec.Number]) { ApplicationArea = All; }
                        }
                    }
                }
                trigger OnOpenPage()
                begin
                    Rec.Reset();
                    Rec.DeleteAll();
                    Rec.Number := 1;
                    Rec.Insert();
                    Rec.FindFirst();
                end;
                var
                    Derived: array[20] of Decimal;
                internal procedure SetHeader(RowNo: Code[20])
                var
                    Row: Record "CPU Row";
                begin
                    Clear(Derived);
                    if Row.Get(RowNo) then
                        Derived[1] := Row.Amount;
                end;
            }
            """);

        File.WriteAllText(Path.Combine(_root, "CardUpdate.Page.al"), """
            page 90353 "CPU Card Update"
            {
                PageType = Card;
                SourceTable = "CPU Row";
                layout
                {
                    area(Content)
                    {
                        group(General)
                        {
                            field("No."; Rec."No.") { ApplicationArea = All; }
                            field(Amount; Rec.Amount)
                            {
                                ApplicationArea = All;
                                trigger OnValidate()
                                begin
                                    Trace.Note('ValidateBegin');
                                    CurrPage.Update(true);
                                    Trace.Note('ValidateEnd');
                                end;
                            }
                        }
                    }
                    area(FactBoxes) { part(Facts; "CPU Facts") { ApplicationArea = All; } }
                }
                trigger OnAfterGetCurrRecord()
                begin
                    Trace.Note('HostAGCR');
                    CurrPage.Facts.Page.SetHeader(Rec."No.");
                end;
                var
                    Trace: Codeunit "CPU Trace";
            }
            """);

        // The exception arm: CurrPage.Update, then an AL Error() before OnValidate returns.
        File.WriteAllText(Path.Combine(_root, "CardRaise.Page.al"), """
            page 90356 "CPU Card Raise"
            {
                PageType = Card;
                SourceTable = "CPU Row";
                layout
                {
                    area(Content)
                    {
                        group(General)
                        {
                            field("No."; Rec."No.") { ApplicationArea = All; }
                            field(Amount; Rec.Amount)
                            {
                                ApplicationArea = All;
                                trigger OnValidate()
                                begin
                                    Trace.Note('ValidateBegin');
                                    CurrPage.Update(true);
                                    Error('refused by the trigger');
                                end;
                            }
                        }
                    }
                    area(FactBoxes) { part(Facts; "CPU Facts") { ApplicationArea = All; } }
                }
                trigger OnAfterGetCurrRecord()
                begin
                    Trace.Note('HostAGCR');
                    CurrPage.Facts.Page.SetHeader(Rec."No.");
                end;
                var
                    Trace: Codeunit "CPU Trace";
            }
            """);

        // The control arm: identical but for the absent CurrPage.Update call.
        File.WriteAllText(Path.Combine(_root, "CardPlain.Page.al"), """
            page 90354 "CPU Card Plain"
            {
                PageType = Card;
                SourceTable = "CPU Row";
                layout
                {
                    area(Content)
                    {
                        group(General)
                        {
                            field("No."; Rec."No.") { ApplicationArea = All; }
                            field(Amount; Rec.Amount)
                            {
                                ApplicationArea = All;
                                trigger OnValidate()
                                begin
                                    Trace.Note('ValidateBegin');
                                    Trace.Note('ValidateEnd');
                                end;
                            }
                        }
                    }
                    area(FactBoxes) { part(Facts; "CPU Facts") { ApplicationArea = All; } }
                }
                trigger OnAfterGetCurrRecord()
                begin
                    Trace.Note('HostAGCR');
                    CurrPage.Facts.Page.SetHeader(Rec."No.");
                end;
                var
                    Trace: Codeunit "CPU Trace";
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Tests.Codeunit.al"), """
            codeunit 90355 "CPU Tests"
            {
                Subtype = Test;
                TestPermissions = Disabled;

                var
                    Trace: Codeunit "CPU Trace";

                local procedure Seed(var Row: Record "CPU Row"; RowNo: Code[20])
                begin
                    Row.Reset();
                    Row.DeleteAll();
                    Row.Init();
                    Row."No." := RowNo;
                    Row.Amount := 20;
                    Row.Insert();
                end;

                local procedure Check(Expected: Variant; Actual: Variant; Msg: Text)
                begin
                    if Format(Expected) <> Format(Actual) then
                        Error('expected <%1> got <%2>: %3', Expected, Actual, Msg);
                end;

                [Test]
                procedure CurrPageUpdate_RefreshesTheFactBoxAfterOnValidateReturns()
                var
                    Row: Record "CPU Row";
                    Card: TestPage "CPU Card Update";
                begin
                    Seed(Row, 'A');
                    Card.OpenEdit();
                    Card.GoToRecord(Row);
                    Card.Facts.First();
                    Check(20, Card.Facts.Derived.AsDecimal(), 'the FactBox before the edit');
                    Trace.Reset();

                    Card.Amount.SetValue(50);

                    Check('ValidateBegin;ValidateEnd;HostAGCR;', Trace.Get(), 'the trigger order');
                    Card.Facts.First();
                    Check(50, Card.Facts.Derived.AsDecimal(), 'the FactBox after the edit');
                    Card.Close();
                end;

                // A trigger that ends in an AL Error() gets no refresh: BC's request dies with
                // the failed trigger, and running page code during an unwinding AL error can
                // replace the error the test is asserting on. The asserterror pins that the
                // ORIGINAL message survives, and the trace pins that no HostAGCR was raised.
                [Test]
                procedure CurrPageUpdateThenError_RaisesNoRefreshAndKeepsTheOriginalError()
                var
                    Row: Record "CPU Row";
                    Card: TestPage "CPU Card Raise";
                begin
                    Seed(Row, 'C');
                    Card.OpenEdit();
                    Card.GoToRecord(Row);
                    Trace.Reset();

                    asserterror Card.Amount.SetValue(50);

                    if StrPos(GetLastErrorText(), 'refused by the trigger') = 0 then
                        Error('the trigger''s own error did not survive: <%1>', GetLastErrorText());
                    Check('ValidateBegin;', Trace.Get(), 'no refresh after a failed trigger');
                end;

                // A regression guard on the "no refresh without a CurrPage.Update in THIS
                // trigger" property, driven across an operation - New() - that runs BC's own
                // NewRecordAsync/SaveRecordAsync outside any AL trigger. The page used here
                // calls CurrPage.Update nowhere at all, so any HostAGCR after the SetValue is
                // a refresh nothing asked for.
                //
                // Honest about what it does NOT prove: it still passes with the
                // `_triggerDepth == 0` drop removed, because those internal raisers carry
                // RecordSaved WITHOUT Update and the flag filter already rejects them. The
                // case that reaches the drop is a PART's CurrPage.Update propagating to its
                // HOST - measured once across the whole corpus, stack recorded in
                // docs/testpage-currpage-update.md - and it needs a host/part pair this
                // fixture does not have.
                [Test]
                procedure ARequestArmedOutsideATrigger_DoesNotFireOnTheNextTrigger()
                var
                    Row: Record "CPU Row";
                    Card: TestPage "CPU Card Plain";
                begin
                    Seed(Row, 'D');
                    Card.OpenEdit();
                    Card.GoToRecord(Row);
                    Card.New();
                    Card."No.".SetValue('D2');
                    Trace.Reset();

                    Card.Amount.SetValue(50);

                    Check('ValidateBegin;ValidateEnd;', Trace.Get(),
                        'no refresh leaked from the New() that preceded this trigger');
                end;

                [Test]
                procedure NoCurrPageUpdate_LeavesTheFactBoxAlone()
                var
                    Row: Record "CPU Row";
                    Card: TestPage "CPU Card Plain";
                begin
                    Seed(Row, 'B');
                    Card.OpenEdit();
                    Card.GoToRecord(Row);
                    Card.Facts.First();
                    Check(20, Card.Facts.Derived.AsDecimal(), 'the FactBox before the edit');
                    Trace.Reset();

                    Card.Amount.SetValue(50);

                    Check('ValidateBegin;ValidateEnd;', Trace.Get(), 'the trigger order');
                    Card.Facts.First();
                    Check(20, Card.Facts.Derived.AsDecimal(), 'the FactBox is unchanged');
                    Card.Close();
                end;
            }
            """);
    }

    private static (int ExitCode, string Output) Spawn(string bundle, string pkgDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundle}\"");
        args.Append($" --package-cache \"{pkgDir}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }
}
