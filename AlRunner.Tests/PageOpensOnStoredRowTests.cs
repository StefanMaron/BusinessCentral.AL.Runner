// PageOpensOnStoredRowTests — issue #4752.
//
// RUNNER-MECHANISM test. The BC claim is corpus codeunit 67361: a page opened on a caller's
// row shows, and saves, the row as the table holds it; a value the caller set in memory reaches
// OnOpenPage only. This pins the runner's wiring for it: RunnerTestClientSession.GetPage
// re-reads a caller-positioned row before the handler gets the page (skipped -> <MEM>/<CALC>).
//
// The fixture declares no "application", per .claude/rules/no-base-app-in-csharp-tests.md.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageOpensOnStoredRowTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public PageOpensOnStoredRowTests()
    {
        _root = TestScratch.Dir("al-runner-page-opens-on-stored-row-4752");
        Directory.CreateDirectory(_root);
        WriteBundle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void PageShowsAndSavesTheStoredRow_NotTheCallersUnsavedValues()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        // Each arm asserts inside AL; the counts distinguish "passed" from "discovered nothing".
        Assert.True(output.Contains("passed 5 ", StringComparison.Ordinal),
            $"expected all five arms to pass; exit={exit}\n{output}");
        Assert.Matches(@"\bfailed 0\b", output);
        Assert.Matches(@"\berrors 0\b", output);
        Assert.Equal(0, exit);
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            {
              "id": "4a7f2c1e-9d35-4b86-8e0a-4752c3d1b9f6",
              "name": "Page Opens On Stored Row Fixture",
              "publisher": "AL Runner Tests",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 90730, "to": 90734 } ],
              "platform": "27.0.0.0",
              "runtime": "15.0",
              "target": "Cloud"
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Objects.al"), """
            table 90730 "POSR Row"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; Descr; Text[50]) { }
                    field(3; Grp; Code[10]) { }
                }
                keys { key(PK; "No.") { Clustered = true; } }
            }

            codeunit 90731 "POSR Probe"
            {
                SingleInstance = true;
                var
                    Edit: Boolean;
                    OpenSeen: Text;
                    Shown: Text;
                procedure Reset(NewEdit: Boolean) begin Edit := NewEdit; OpenSeen := ''; Shown := ''; end;
                procedure GetEdit(): Boolean begin exit(Edit); end;
                procedure SetOpenSeen(Value: Text) begin OpenSeen := Value; end;
                procedure GetOpenSeen(): Text begin exit(OpenSeen); end;
                procedure SetShown(Value: Text) begin Shown := Value; end;
                procedure GetShown(): Text begin exit(Shown); end;
            }

            page 90732 "POSR Target"
            {
                PageType = Card;
                SourceTable = "POSR Row";
                ApplicationArea = All;
                layout
                {
                    area(Content)
                    {
                        group(General)
                        {
                            field("No."; Rec."No.") { ApplicationArea = All; }
                            field(Descr; Rec.Descr) { ApplicationArea = All; }
                            field(Grp; Rec.Grp) { ApplicationArea = All; }
                        }
                    }
                }
                trigger OnOpenPage()
                var
                    Probe: Codeunit "POSR Probe";
                begin
                    Probe.SetOpenSeen(Rec."No." + ':' + Rec.Grp);
                end;
            }

            page 90733 "POSR Host"
            {
                PageType = List;
                SourceTable = "POSR Row";
                ApplicationArea = All;
                UsageCategory = Lists;
                layout
                {
                    area(Content)
                    {
                        repeater(Rows)
                        {
                            field("No."; Rec."No.") { ApplicationArea = All; }
                            field(Grp; Rec.Grp) { ApplicationArea = All; }
                        }
                    }
                }
                actions
                {
                    area(Processing)
                    {
                        action(RunTarget)
                        {
                            ApplicationArea = All;
                            RunObject = Page "POSR Target";
                            RunPageOnRec = true;
                        }
                    }
                }
                trigger OnAfterGetRecord()
                begin
                    Rec.Grp := 'CALC';
                end;
            }

            codeunit 90734 "POSR Tests"
            {
                Subtype = Test;

                local procedure Seed(Edit: Boolean)
                var
                    Row: Record "POSR Row";
                    Probe: Codeunit "POSR Probe";
                begin
                    Probe.Reset(Edit);
                    Row.DeleteAll();
                    Row."No." := 'A'; Row.Grp := 'G1'; Row.Insert();
                    Row."No." := 'B'; Row.Grp := 'G2'; Row.Insert();
                end;

                local procedure Check(Expected: Text; Actual: Text; What: Text)
                begin
                    if Expected <> Actual then
                        Error('%1: expected <%2>, got <%3>', What, Expected, Actual);
                end;

                // Fails with <CALC> when the target shows the host's in-memory row.
                [Test]
                [HandlerFunctions('TargetHandler')]
                procedure RunPageOnRecTargetShowsTheStoredRow()
                var
                    Probe: Codeunit "POSR Probe";
                    Host: TestPage "POSR Host";
                begin
                    Seed(false);
                    Host.OpenEdit();
                    Host.Last();
                    Check('CALC', Host.Grp.Value(), 'precondition: the host computed Grp');
                    Host.RunTarget.Invoke();
                    Check('B:CALC', Probe.GetOpenSeen(), 'OnOpenPage sees the host''s row as the host holds it');
                    Check('G2', Probe.GetShown(), 'the target shows the stored Grp');
                end;

                // Fails with <CALC> when the target's save writes the host's computed value.
                [Test]
                [HandlerFunctions('TargetHandler')]
                procedure RunPageOnRecTargetSaveKeepsTheStoredValue()
                var
                    Row: Record "POSR Row";
                    Host: TestPage "POSR Host";
                begin
                    Seed(true);
                    Host.OpenEdit();
                    Host.Last();
                    Host.RunTarget.Invoke();
                    Row.Get('B');
                    Check('Written', Row.Descr, 'precondition: the target''s save reached the table');
                    Check('G2', Row.Grp, 'the stored Grp after the target''s save');
                end;

                // Fails with <MEM> when Page.Run shows the caller's unsaved value.
                [Test]
                [HandlerFunctions('TargetHandler')]
                procedure PageRunShowsTheStoredRow()
                var
                    Row: Record "POSR Row";
                    Probe: Codeunit "POSR Probe";
                begin
                    Seed(false);
                    Row.Get('B');
                    Row.Grp := 'MEM';
                    Page.Run(Page::"POSR Target", Row);
                    Check('B:MEM', Probe.GetOpenSeen(), 'OnOpenPage sees the caller''s record');
                    Check('G2', Probe.GetShown(), 'the page shows the stored Grp');
                end;

                // Control: a row the table does not hold keeps the caller's values rather than
                // being blanked by a failed read.
                [Test]
                [HandlerFunctions('TargetHandler')]
                procedure PageRunOnARowNotInTheTableKeepsTheCallersValues()
                var
                    Row: Record "POSR Row";
                    Probe: Codeunit "POSR Probe";
                begin
                    Seed(false);
                    Row.Init();
                    Row."No." := 'Z';
                    Row.Grp := 'MEM';
                    Page.Run(Page::"POSR Target", Row);
                    Check('MEM', Probe.GetShown(), 'the page on a row the table does not hold');
                end;

                // Control: nothing to re-read when the caller changed nothing.
                [Test]
                [HandlerFunctions('TargetHandler')]
                procedure PageRunOnAnUnchangedRowShowsIt()
                var
                    Row: Record "POSR Row";
                    Probe: Codeunit "POSR Probe";
                begin
                    Seed(false);
                    Row.Get('A');
                    Page.Run(Page::"POSR Target", Row);
                    Check('G1', Probe.GetShown(), 'the page on an unchanged row');
                end;

                [PageHandler]
                procedure TargetHandler(var Target: TestPage "POSR Target")
                var
                    Probe: Codeunit "POSR Probe";
                begin
                    Probe.SetShown(Target.Grp.Value());
                    if Probe.GetEdit() then begin
                        Target.Descr.SetValue('Written');
                        Target.Close();
                    end;
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
