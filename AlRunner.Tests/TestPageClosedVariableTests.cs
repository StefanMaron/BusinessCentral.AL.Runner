// TestPageClosedVariableTests — issues #4713, #4722, #4729.
//
// Runner mechanism: NavTestPageBase.Close() detaches the TestPage variable (BC's InternalClear sets
// testPage = null), and the runner stands in for that with LiveNavTestPage's detach flag, read by
// the rewritten CheckPageOpened. What BC does after a Close() is measured upstream by corpus
// codeunit 60419 "QCV Close Veto Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#431); this
// fixture pins the runner's two halves of it: the detach fires after an allowed close and after a
// refused one, and reopening the same variable clears it -- after a refused close too (#4729).
// A variable starts detached (#4722, corpus codeunit 67040): never-opened Close / field read /
// GoToRecord raise, while pages BC attaches through Trap() or hands to a [PageHandler] are open.
//
// The fixture declares no "application", per .claude/rules/no-base-app-in-csharp-tests.md.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageClosedVariableTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageClosedVariableTests()
    {
        _root = TestScratch.Dir("al-runner-tpcv-4713");
        Directory.CreateDirectory(_root);
        WriteBundle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void ATestPageVariable_IsOpenOnlyBetweenOpenAndClose()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        var m = Regex.Match(output, @"Tests:\s+(\d+)\s+passed\s+(\d+)\s+failed\s+(\d+)");
        Assert.True(m.Success, $"no Tests: summary line; exit={exit}\n{output}");
        Assert.True(m.Groups[1].Value == "11" && m.Groups[2].Value == "11" && m.Groups[3].Value == "0",
            $"expected all eleven arms to pass; exit={exit}\n{output}");
        Assert.Equal(0, exit);
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            {
              "id": "7c1e4a52-9d3b-4f60-a8e2-4713c105e0b2",
              "name": "TestPage Closed Variable Fixture",
              "publisher": "AL Runner Tests",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 90713, "to": 90719 } ],
              "platform": "27.0.0.0",
              "runtime": "15.0",
              "target": "Cloud"
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Probe.Codeunit.al"), """
            codeunit 90713 "TPCV Probe"
            {
                SingleInstance = true;
                var
                    Mode: Integer;
                    QueryCloseCount: Integer;
                procedure Reset(NewMode: Integer) begin Mode := NewMode; QueryCloseCount := 0; end;
                procedure AnswerQueryClose(): Boolean
                begin
                    QueryCloseCount += 1;
                    if Mode = 1 then
                        Error('TPCV close refused');
                    exit(true);
                end;
                procedure QueryCloseCalls(): Integer begin exit(QueryCloseCount); end;
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Card.Page.al"), """
            page 90714 "TPCV Card"
            {
                PageType = Card;
                ApplicationArea = All;
                UsageCategory = Administration;
                layout
                {
                    area(Content)
                    {
                        field(Marker; MarkerVar) { ApplicationArea = All; }
                    }
                }
                var
                    Probe: Codeunit "TPCV Probe";
                    MarkerVar: Text[10];
                trigger OnOpenPage()
                begin
                    MarkerVar := 'OPENED';
                end;
                trigger OnQueryClosePage(CloseAction: Action): Boolean
                begin
                    exit(Probe.AnswerQueryClose());
                end;
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Row.Table.al"), """
            table 90716 "TPCV Row"
            {
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; Descr; Text[50]) { }
                }
                keys { key(PK; "No.") { Clustered = true; } }
            }
            """);

        File.WriteAllText(Path.Combine(_root, "RowCard.Page.al"), """
            page 90717 "TPCV Row Card"
            {
                PageType = Card;
                SourceTable = "TPCV Row";
                ApplicationArea = All;
                UsageCategory = Administration;
                layout
                {
                    area(Content)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                        field(Descr; Rec.Descr) { ApplicationArea = All; }
                    }
                }
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Tests.Codeunit.al"), """
            codeunit 90715 "TPCV Tests"
            {
                Subtype = Test;
                var
                    Probe: Codeunit "TPCV Probe";
                    HandlerDescr: Text;

                [Test]
                [HandlerFunctions('ConsumeMessage')]
                procedure RefusedClose_ReopenIsOpenAgain()
                var
                    Card: TestPage "TPCV Card";
                begin
                    Probe.Reset(1);
                    Card.OpenEdit();
                    Card.Close();
                    Probe.Reset(0);
                    Card.OpenView();
                    if Card.Marker.Value() <> 'OPENED' then Error('reopened after refusal: %1', Card.Marker.Value());
                end;

                [Test]
                procedure NeverOpened_CloseRaisesNotOpen()
                var
                    Card: TestPage "TPCV Card";
                begin
                    asserterror Card.Close();
                    ExpectNotOpen('never-opened close');
                end;

                [Test]
                procedure NeverOpened_FieldReadRaisesNotOpen()
                var
                    Card: TestPage "TPCV Card";
                    Ignored: Text;
                begin
                    asserterror Ignored := Card.Marker.Value();
                    ExpectNotOpen('never-opened field read');
                end;

                [Test]
                procedure NeverOpened_GoToRecordRaisesNotOpen()
                var
                    Row: Record "TPCV Row";
                    Card: TestPage "TPCV Row Card";
                begin
                    InsertRow(Row);
                    asserterror Card.GoToRecord(Row);
                    ExpectNotOpen('never-opened GoToRecord');
                    Card.OpenView();
                    if not Card.GoToRecord(Row) then Error('opened GoToRecord found nothing');
                    if Card.Descr.Value() <> 'First row' then Error('opened GoToRecord: %1', Card.Descr.Value());
                end;

                [Test]
                procedure Trapped_PageRunIsOpen()
                var
                    Row: Record "TPCV Row";
                    Card: TestPage "TPCV Row Card";
                begin
                    InsertRow(Row);
                    Card.Trap();
                    Page.Run(Page::"TPCV Row Card", Row);
                    if Card.Descr.Value() <> 'First row' then Error('trapped: %1', Card.Descr.Value());
                    Card.Close();
                end;

                [Test]
                [HandlerFunctions('RowCardHandler')]
                procedure PageHandler_PageIsOpen()
                var
                    Row: Record "TPCV Row";
                begin
                    InsertRow(Row);
                    HandlerDescr := '';
                    Page.Run(Page::"TPCV Row Card", Row);
                    if HandlerDescr <> 'First row' then Error('handler: %1', HandlerDescr);
                end;

                local procedure InsertRow(var Row: Record "TPCV Row")
                begin
                    Row.DeleteAll();
                    Row."No." := 'R1';
                    Row.Descr := 'First row';
                    Row.Insert();
                end;

                [PageHandler]
                procedure RowCardHandler(var Card: TestPage "TPCV Row Card")
                begin
                    HandlerDescr := Card.Descr.Value();
                end;

                [Test]
                procedure AllowedClose_SecondCloseRaisesNotOpen()
                var
                    Card: TestPage "TPCV Card";
                begin
                    Probe.Reset(0);
                    Card.OpenEdit();
                    Card.Close();
                    asserterror Card.Close();
                    ExpectNotOpen('allowed second close');
                    if Probe.QueryCloseCalls() <> 1 then Error('allowed: QueryClose %1', Probe.QueryCloseCalls());
                end;

                [Test]
                procedure AllowedClose_FieldReadRaisesNotOpen()
                var
                    Card: TestPage "TPCV Card";
                    Ignored: Text;
                begin
                    Probe.Reset(0);
                    Card.OpenEdit();
                    if Card.Marker.Value() <> 'OPENED' then Error('before close: %1', Card.Marker.Value());
                    Card.Close();
                    asserterror Ignored := Card.Marker.Value();
                    ExpectNotOpen('allowed field read');
                end;

                [Test]
                [HandlerFunctions('ConsumeMessage')]
                procedure RefusedClose_SecondCloseRaisesNotOpen()
                var
                    Card: TestPage "TPCV Card";
                begin
                    Probe.Reset(1);
                    Card.OpenEdit();
                    Card.Close();
                    asserterror Card.Close();
                    ExpectNotOpen('refused second close');
                    if Probe.QueryCloseCalls() <> 1 then Error('refused: QueryClose %1', Probe.QueryCloseCalls());
                end;

                [Test]
                procedure ReopenAfterClose_IsOpenAgain()
                var
                    Card: TestPage "TPCV Card";
                begin
                    Probe.Reset(0);
                    Card.OpenEdit();
                    Card.Close();
                    Card.OpenView();
                    if Card.Marker.Value() <> 'OPENED' then Error('reopened: %1', Card.Marker.Value());
                    Card.Close();
                    if Probe.QueryCloseCalls() <> 2 then Error('reopen: QueryClose %1', Probe.QueryCloseCalls());
                end;

                [Test]
                procedure NeverClosed_StaysReadable()
                var
                    Card: TestPage "TPCV Card";
                begin
                    Probe.Reset(0);
                    Card.OpenEdit();
                    if Card.Marker.Value() <> 'OPENED' then Error('open: %1', Card.Marker.Value());
                    if Card.Marker.Value() <> 'OPENED' then Error('open, second read: %1', Card.Marker.Value());
                end;

                local procedure ExpectNotOpen(Arm: Text)
                begin
                    if StrPos(GetLastErrorText(), 'The TestPage is not open') = 0 then
                        Error('%1: expected "The TestPage is not open", got "%2"', Arm, GetLastErrorText());
                end;

                [MessageHandler]
                procedure ConsumeMessage(Msg: Text[1024])
                begin
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
