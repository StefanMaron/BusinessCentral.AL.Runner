// PageRunClonesCallersRecordTests — issue #4634.
//
// RUNNER-MECHANISM test. The BC claim (a page opened on a caller's record works on its own copy,
// so the page moving its Rec leaves the caller where it was) is corpus codeunit 67351. This pins
// the runner's own wiring for it: BcRuntime.ConstructFormForStaticEntry binds a caller-supplied
// record with SetSourceTable(record, clone: true). Binding it with clone: false shares the
// caller's cursor, and every arm below then reads <Echo> instead of <Bravo>.
//
// The fixture declares no "application", per .claude/rules/no-base-app-in-csharp-tests.md.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageRunClonesCallersRecordTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public PageRunClonesCallersRecordTests()
    {
        _root = TestScratch.Dir("al-runner-pagerun-clones-callers-record-4634");
        Directory.CreateDirectory(_root);
        WriteBundle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void PageMovingItsRec_LeavesTheCallerAndTheHostWhereTheyWere()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        // Each arm asserts inside AL; the counts distinguish "passed" from "discovered nothing".
        Assert.True(output.Contains("passed 4 ", StringComparison.Ordinal),
            $"expected all four arms to pass; exit={exit}\n{output}");
        Assert.Matches(@"\bfailed 0\b", output);
        Assert.Matches(@"\berrors 0\b", output);
        Assert.Equal(0, exit);
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            {
              "id": "9c1d4e7a-3b52-4f08-a6d1-4634b2e8c0f5",
              "name": "Page Run Clones Callers Record Fixture",
              "publisher": "AL Runner Tests",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 90470, "to": 90476 } ],
              "platform": "27.0.0.0",
              "runtime": "15.0",
              "target": "Cloud"
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Objects.al"), """
            table 90470 "PRCR Row"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; Descr; Text[50]) { }
                }
                keys { key(PK; "No.") { Clustered = true; } }
            }

            codeunit 90471 "PRCR Probe"
            {
                SingleInstance = true;
                var
                    Seen: Text;
                    MovedTo: Text;
                procedure Reset() begin Seen := ''; MovedTo := ''; end;
                procedure SetSeen(Value: Text) begin Seen := Value; end;
                procedure GetSeen(): Text begin exit(Seen); end;
                procedure SetMovedTo(Value: Text) begin MovedTo := Value; end;
                procedure GetMovedTo(): Text begin exit(MovedTo); end;
            }

            page 90472 "PRCR Target"
            {
                PageType = Card;
                SourceTable = "PRCR Row";
                ApplicationArea = All;
                layout
                {
                    area(Content)
                    {
                        group(General)
                        {
                            field("No."; Rec."No.") { ApplicationArea = All; }
                            field(Descr; Rec.Descr) { ApplicationArea = All; }
                        }
                    }
                }
                trigger OnOpenPage()
                var
                    Probe: Codeunit "PRCR Probe";
                begin
                    Probe.SetSeen(Rec.Descr);
                    Rec.Reset();
                    Rec.FindLast();
                    Probe.SetMovedTo(Rec.Descr);
                end;
            }

            page 90473 "PRCR Host"
            {
                PageType = List;
                SourceTable = "PRCR Row";
                ApplicationArea = All;
                UsageCategory = Lists;
                layout
                {
                    area(Content)
                    {
                        repeater(Rows)
                        {
                            field("No."; Rec."No.") { ApplicationArea = All; }
                            field(Descr; Rec.Descr) { ApplicationArea = All; }
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
                            RunObject = Page "PRCR Target";
                            RunPageOnRec = true;
                        }
                    }
                }
            }

            codeunit 90474 "PRCR Tests"
            {
                Subtype = Test;

                local procedure Seed()
                var
                    Row: Record "PRCR Row";
                    Probe: Codeunit "PRCR Probe";
                begin
                    Probe.Reset();
                    Row.DeleteAll();
                    Row."No." := 'A'; Row.Descr := 'Alpha'; Row.Insert();
                    Row."No." := 'B'; Row.Descr := 'Bravo'; Row.Insert();
                    Row."No." := 'C'; Row.Descr := 'Charlie'; Row.Insert();
                end;

                local procedure Check(Expected: Text; Actual: Text; What: Text)
                begin
                    if Expected <> Actual then
                        Error('%1: expected <%2>, got <%3>', What, Expected, Actual);
                end;

                // Fails with <Charlie> when the page shares the caller's record.
                [Test]
                [HandlerFunctions('TargetHandler')]
                procedure PageRunLeavesTheCallersRecord()
                var
                    Row: Record "PRCR Row";
                    Probe: Codeunit "PRCR Probe";
                begin
                    Seed();
                    Row.Get('B');
                    Page.Run(Page::"PRCR Target", Row);
                    Check('Bravo', Probe.GetSeen(), 'the page opens on the caller''s row');
                    Check('Charlie', Probe.GetMovedTo(), 'the page moved its own Rec');
                    Check('Bravo', Row.Descr, 'the caller''s record after Page.Run');
                end;

                // Fails with <Charlie> when the page shares the caller's record.
                [Test]
                [HandlerFunctions('TargetModalHandler')]
                procedure PageRunModalLeavesTheCallersRecord()
                var
                    Row: Record "PRCR Row";
                    Probe: Codeunit "PRCR Probe";
                begin
                    Seed();
                    Row.Get('B');
                    Page.RunModal(Page::"PRCR Target", Row);
                    Check('Charlie', Probe.GetMovedTo(), 'the page moved its own Rec');
                    Check('Bravo', Row.Descr, 'the caller''s record after Page.RunModal');
                end;

                // Fails with <Charlie> when the RunObject target shares the host's record.
                [Test]
                [HandlerFunctions('TargetHandler')]
                procedure RunObjectPageLeavesTheHost()
                var
                    Probe: Codeunit "PRCR Probe";
                    Host: TestPage "PRCR Host";
                begin
                    Seed();
                    Host.OpenEdit();
                    Host.First();
                    Host.Next();
                    Host.RunTarget.Invoke();
                    Check('Bravo', Probe.GetSeen(), 'RunPageOnRec opens the target on the host''s row');
                    Check('Charlie', Probe.GetMovedTo(), 'the target moved its own Rec');
                    Check('Bravo', Host.Descr.Value(), 'the host after the target moved its Rec');
                end;

                // Control: nothing moves when the page does not open.
                [Test]
                procedure UnopenedPageLeavesTheCallersRecord()
                var
                    Row: Record "PRCR Row";
                    Probe: Codeunit "PRCR Probe";
                begin
                    Seed();
                    Row.Get('B');
                    Check('', Probe.GetMovedTo(), 'nothing opened');
                    Check('Bravo', Row.Descr, 'the caller''s record with no page run');
                end;

                [PageHandler]
                procedure TargetHandler(var Target: TestPage "PRCR Target")
                begin
                end;

                [ModalPageHandler]
                procedure TargetModalHandler(var Target: TestPage "PRCR Target")
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
