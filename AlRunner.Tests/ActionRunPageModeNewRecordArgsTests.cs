// ActionRunPageModeNewRecordArgsTests — issues #5014 and #5015.
//
// RUNNER-MECHANISM test. The BC claim is corpus codeunit 67036 "ARPX Tests"
// (StefanMaron/BusinessCentral.AL.Language.Tests#514). This pins the runner's wiring for it:
//   * every Create-mode open (OpenNew, an action answered by a handler, by Trap(), or by nothing)
//     hands OnNewRecord BelowxRec = true, so BC's NavForm.NewRecordAsync puts xRec on the last row
//     (#5015; dropped -> below=No,x=);
//   * a dialog target with no [ModalPageHandler] still runs OnNewRecord before the refusal
//     (#5014; dropped -> <open>).
//
// The fixture declares no "application", per .claude/rules/no-base-app-in-csharp-tests.md.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class ActionRunPageModeNewRecordArgsTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public ActionRunPageModeNewRecordArgsTests()
    {
        _root = TestScratch.Dir("al-runner-action-runpagemode-newrecord-args-5015");
        Directory.CreateDirectory(_root);
        WriteBundle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void CreateModeOpens_PassBelowxRecTrue_AndADialogWithNoHandlerStillStartsTheRecord()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        // Each arm asserts inside AL; the counts distinguish "passed" from "discovered nothing".
        Assert.True(output.Contains("passed 8 ", StringComparison.Ordinal),
            $"expected all eight arms to pass; exit={exit}\n{output}");
        Assert.Matches(@"\bfailed 0\b", output);
        Assert.Matches(@"\berrors 0\b", output);
        Assert.Equal(0, exit);
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            {
              "id": "3e8b1f64-7a20-4c59-9d13-5015c0a4e7b2",
              "name": "Action RunPageMode NewRecord Args Fixture",
              "publisher": "AL Runner Tests",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 90770, "to": 90776 } ],
              "platform": "27.0.0.0",
              "runtime": "15.0",
              "target": "Cloud"
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Objects.al"), """
            table 90770 "RPMX Row"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; Name; Text[50]) { }
                }
                keys { key(PK; "No.") { Clustered = true; } }
            }

            table 90771 "RPMX Host Row"
            {
                DataClassification = CustomerContent;
                fields { field(1; Code; Code[20]) { } }
                keys { key(PK; Code) { Clustered = true; } }
            }

            codeunit 90772 "RPMX Probe"
            {
                SingleInstance = true;
                var
                    Trail: Text;
                procedure Reset() begin Trail := ''; end;
                procedure Note(TriggerName: Text)
                begin
                    if Trail <> '' then
                        Trail += ',';
                    Trail += TriggerName;
                end;
                procedure Triggers(): Text begin exit(Trail); end;
            }

            page 90773 "RPMX Card"
            {
                PageType = Card;
                SourceTable = "RPMX Row";
                ApplicationArea = All;
                layout
                {
                    area(Content)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                    }
                }
                trigger OnOpenPage()
                var
                    Probe: Codeunit "RPMX Probe";
                begin
                    Probe.Note('open');
                end;

                trigger OnNewRecord(BelowxRec: Boolean)
                var
                    Probe: Codeunit "RPMX Probe";
                begin
                    Probe.Note('new:below=' + Format(BelowxRec) + ',x=' + xRec."No." + ',no=' + Rec."No.");
                end;
            }

            page 90774 "RPMX Dialog"
            {
                PageType = StandardDialog;
                SourceTable = "RPMX Row";
                ApplicationArea = All;
                layout
                {
                    area(Content)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                    }
                }
                trigger OnOpenPage()
                var
                    Probe: Codeunit "RPMX Probe";
                begin
                    Probe.Note('open');
                end;

                trigger OnNewRecord(BelowxRec: Boolean)
                var
                    Probe: Codeunit "RPMX Probe";
                begin
                    Probe.Note('new:below=' + Format(BelowxRec) + ',x=' + xRec."No." + ',no=' + Rec."No.");
                end;
            }

            page 90775 "RPMX Host"
            {
                PageType = Card;
                SourceTable = "RPMX Host Row";
                ApplicationArea = All;
                layout
                {
                    area(Content)
                    {
                        field(Code; Rec.Code) { ApplicationArea = All; }
                    }
                }
                actions
                {
                    area(Processing)
                    {
                        action(OpenCreate)
                        {
                            ApplicationArea = All;
                            RunObject = page "RPMX Card";
                            RunPageMode = Create;
                        }
                        action(OpenCreateDialog)
                        {
                            ApplicationArea = All;
                            RunObject = page "RPMX Dialog";
                            RunPageMode = Create;
                        }
                    }
                }
            }

            codeunit 90776 "RPMX Tests"
            {
                Subtype = Test;

                local procedure Seed(WithRows: Boolean)
                var
                    Row: Record "RPMX Row";
                    HostRow: Record "RPMX Host Row";
                    Probe: Codeunit "RPMX Probe";
                begin
                    Probe.Reset();
                    Row.DeleteAll();
                    HostRow.DeleteAll();
                    HostRow.Code := 'H'; HostRow.Insert();
                    if WithRows then begin
                        Row."No." := 'A'; Row.Name := 'Alpha'; Row.Insert();
                        Row."No." := 'B'; Row.Name := 'Bravo'; Row.Insert();
                    end;
                end;

                local procedure OpenHost(var Host: TestPage "RPMX Host"; WithRows: Boolean)
                begin
                    Seed(WithRows);
                    Host.OpenEdit();
                    Host.GoToKey('H');
                end;

                local procedure Check(Expected: Text; What: Text)
                var
                    Probe: Codeunit "RPMX Probe";
                begin
                    if Expected <> Probe.Triggers() then
                        Error('%1: expected <%2>, got <%3>', What, Expected, Probe.Triggers());
                end;

                local procedure CheckRefused(What: Text)
                begin
                    if StrPos(GetLastErrorText(), 'must have a handler') = 0 then
                        Error('%1: expected BC''s no-handler refusal, got <%2>', What, GetLastErrorText());
                end;

                // Fails with below=No,x= when a Create-mode open passes BelowxRec = false (#5015).
                [Test]
                procedure OpenNew_TableWithRows()
                var
                    Card: TestPage "RPMX Card";
                begin
                    Seed(true);
                    Card.OpenNew();
                    Card.Close();
                    Check('open,new:below=Yes,x=B,no=', 'OpenNew, rows');
                end;

                // The empty table: BelowxRec is still true, and there is no last row for xRec.
                [Test]
                procedure OpenNew_EmptyTable()
                var
                    Card: TestPage "RPMX Card";
                begin
                    Seed(false);
                    Card.OpenNew();
                    Card.Close();
                    Check('open,new:below=Yes,x=,no=', 'OpenNew, empty');
                end;

                [Test]
                [HandlerFunctions('CardCloseHandler')]
                procedure ActionCreate_Handler()
                var
                    Host: TestPage "RPMX Host";
                begin
                    OpenHost(Host, true);
                    Host.OpenCreate.Invoke();
                    Host.Close();
                    Check('open,new:below=Yes,x=B,no=', 'Create action, page handler');
                end;

                [Test]
                procedure ActionCreate_NoHandler()
                var
                    Host: TestPage "RPMX Host";
                begin
                    OpenHost(Host, true);
                    Host.OpenCreate.Invoke();
                    Host.Close();
                    Check('open,new:below=Yes,x=B,no=', 'Create action, nothing bound');
                end;

                [Test]
                procedure ActionCreate_Trapped()
                var
                    Host: TestPage "RPMX Host";
                    Card: TestPage "RPMX Card";
                begin
                    OpenHost(Host, true);
                    Card.Trap();
                    Host.OpenCreate.Invoke();
                    Card.Close();
                    Host.Close();
                    Check('open,new:below=Yes,x=B,no=', 'Create action, trapped');
                end;

                [Test]
                [HandlerFunctions('DialogCancelHandler')]
                procedure DialogCreate_ModalHandler()
                var
                    Host: TestPage "RPMX Host";
                begin
                    OpenHost(Host, true);
                    Host.OpenCreateDialog.Invoke();
                    Host.Close();
                    Check('open,new:below=Yes,x=B,no=', 'Create action on a dialog, modal handler');
                end;

                // Fails with <open> when the dialog's no-handler refusal drops the Create mark (#5014).
                [Test]
                procedure DialogCreate_NoHandler()
                var
                    Host: TestPage "RPMX Host";
                begin
                    OpenHost(Host, true);
                    asserterror Host.OpenCreateDialog.Invoke();
                    CheckRefused('Create action on a dialog, nothing bound');
                    Check('open,new:below=Yes,x=B,no=', 'Create action on a dialog, nothing bound');
                end;

                [Test]
                procedure DialogCreate_NoHandler_EmptyTable()
                var
                    Host: TestPage "RPMX Host";
                begin
                    OpenHost(Host, false);
                    asserterror Host.OpenCreateDialog.Invoke();
                    CheckRefused('Create action on a dialog, nothing bound, empty');
                    Check('open,new:below=Yes,x=,no=', 'Create action on a dialog, nothing bound, empty');
                end;

                [PageHandler]
                procedure CardCloseHandler(var Card: TestPage "RPMX Card")
                begin
                    Card.Close();
                end;

                [ModalPageHandler]
                procedure DialogCancelHandler(var Dialog: TestPage "RPMX Dialog")
                begin
                    Dialog.Cancel().Invoke();
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
