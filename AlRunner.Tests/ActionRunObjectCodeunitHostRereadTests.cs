// ActionRunObjectCodeunitHostRereadTests — issue #4589.
//
// RUNNER-MECHANISM test. The BC claim (a RunObject codeunit's Modify shows on the host TestPage,
// and the codeunit moving its Rec does not move the host) is corpus codeunit 60606, adjudicated
// on the corpus's service-tier legs. This pins the runner's own wiring for it:
// RunnerPageInstance.RunTargetCodeunit hands the codeunit a COPY of the host row, then
// RereadHostRowAfterAction re-reads the row into the host's buffer through a fresh record.
// Removing the TransferFields leaves the host on 'Bravo'; handing the codeunit the live record
// moves the host to 'Echo'. Each arm below goes red for exactly one of those.
//
// The fixture declares no "application", per .claude/rules/no-base-app-in-csharp-tests.md.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class ActionRunObjectCodeunitHostRereadTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public ActionRunObjectCodeunitHostRereadTests()
    {
        _root = TestScratch.Dir("al-runner-runobject-codeunit-reread-4589");
        Directory.CreateDirectory(_root);
        WriteBundle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void RunObjectCodeunit_WriteShowsOnHost_AndMovingItsRecLeavesTheHostAlone()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        // Each arm asserts inside AL; the counts distinguish "passed" from "discovered nothing".
        Assert.True(output.Contains("pass:        3"),
            $"expected all three arms to pass; exit={exit}\n{output}");
        Assert.Matches(@"fail:\s+0\b", output);
        Assert.Equal(0, exit);
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            {
              "id": "5f0b3c2e-8a41-4d7e-b6c9-2e4589a1c0d7",
              "name": "RunObject Codeunit Host Reread Fixture",
              "publisher": "AL Runner Tests",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 90458, "to": 90464 } ],
              "platform": "27.0.0.0",
              "runtime": "15.0",
              "target": "Cloud"
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Objects.al"), """
            table 90458 "ROCR Row"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; Descr; Text[50]) { }
                }
                keys { key(PK; "No.") { Clustered = true; } }
            }

            codeunit 90459 "ROCR Mode"
            {
                SingleInstance = true;
                var
                    Mode: Text;
                procedure SetMode(NewMode: Text) begin Mode := NewMode; end;
                procedure GetMode(): Text begin exit(Mode); end;
            }

            codeunit 90460 "ROCR Target"
            {
                TableNo = "ROCR Row";
                trigger OnRun()
                var
                    Mode: Codeunit "ROCR Mode";
                begin
                    case Mode.GetMode() of
                        'WRITE':
                            begin
                                Rec.Descr := 'Written';
                                Rec.Modify();
                            end;
                        'MOVE':
                            Rec.FindLast();
                    end;
                end;
            }

            page 90461 "ROCR Host"
            {
                PageType = List;
                SourceTable = "ROCR Row";
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
                            RunObject = Codeunit "ROCR Target";
                        }
                    }
                }
            }

            codeunit 90462 "ROCR Tests"
            {
                Subtype = Test;

                local procedure Seed(Mode: Text)
                var
                    Row: Record "ROCR Row";
                    M: Codeunit "ROCR Mode";
                begin
                    M.SetMode(Mode);
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

                // Fails with <Bravo> when RereadHostRowAfterAction does not copy the row back.
                [Test]
                procedure WriteByTheCodeunitShowsOnTheHost()
                var
                    Host: TestPage "ROCR Host";
                begin
                    Seed('WRITE');
                    Host.OpenEdit();
                    Host.First();
                    Host.Next();
                    Host.RunTarget.Invoke();
                    Check('B', Host."No.".Value(), 'host row');
                    Check('Written', Host.Descr.Value(), 'host value after the codeunit wrote');
                end;

                // Fails with <Charlie> when the codeunit is handed the host's live record.
                [Test]
                procedure MoveByTheCodeunitLeavesTheHostOnItsRow()
                var
                    Host: TestPage "ROCR Host";
                begin
                    Seed('MOVE');
                    Host.OpenEdit();
                    Host.First();
                    Host.Next();
                    Host.RunTarget.Invoke();
                    Check('Bravo', Host.Descr.Value(), 'host row after the codeunit moved its Rec');
                end;

                // Control: a codeunit that does nothing leaves the host exactly as it was.
                [Test]
                procedure IdleCodeunitLeavesTheHostUnchanged()
                var
                    Host: TestPage "ROCR Host";
                begin
                    Seed('IDLE');
                    Host.OpenEdit();
                    Host.First();
                    Host.Next();
                    Host.RunTarget.Invoke();
                    Check('Bravo', Host.Descr.Value(), 'host value after an idle codeunit');
                    Host.Next();
                    Check('C', Host."No.".Value(), 'host cursor after an idle codeunit');
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
