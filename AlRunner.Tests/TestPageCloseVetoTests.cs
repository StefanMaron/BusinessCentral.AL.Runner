// TestPageCloseVetoTests — issue #4710.
//
// Runner claim only: TestPage.Close() on a page whose OnQueryClosePage vetoes the close no longer
// raises the "testpage-close-veto" RunnerOutOfScopeException, so the AL after the Close() runs.
// What BC does there — Close() returns, OnClosePage does not run — is measured upstream by corpus
// codeunit 60419 "QCV Close Veto Tests" and is not re-asserted as BC evidence here.
//
// Three arms, each a [Test] asserting inside AL: a plain exit(false) veto, the Confirm-answered-No
// veto the Base Application's User Card uses, and the allowed close as the control — so "never
// runs OnClosePage" and "always refuses" both fail a named arm.
//
// The fixture declares no "application", per .claude/rules/no-base-app-in-csharp-tests.md.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageCloseVetoTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageCloseVetoTests()
    {
        _root = TestScratch.Dir("al-runner-tcv-4710");
        Directory.CreateDirectory(_root);
        WriteBundle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void CloseOnAVetoingPage_ReturnsToTheTest_AndRunsNoOnClosePage()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        var m = Regex.Match(output, @"Tests:\s+(\d+)\s+passed\s+(\d+)\s+failed\s+(\d+)");
        Assert.True(m.Success, $"no Tests: summary line; exit={exit}\n{output}");
        Assert.True(m.Groups[1].Value == "3" && m.Groups[2].Value == "3" && m.Groups[3].Value == "0",
            $"expected all three arms to pass; exit={exit}\n{output}");
        Assert.Equal(0, exit);
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            {
              "id": "5f0c2a6e-3b7d-4e19-8c44-4710c105e0a1",
              "name": "TestPage Close Veto Fixture",
              "publisher": "AL Runner Tests",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 90470, "to": 90479 } ],
              "platform": "27.0.0.0",
              "runtime": "15.0",
              "target": "Cloud"
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Probe.Codeunit.al"), """
            codeunit 90470 "TCV Probe"
            {
                SingleInstance = true;
                var
                    Mode: Integer;
                    QueryCloseCount: Integer;
                    ClosePageCount: Integer;
                procedure Reset(NewMode: Integer) begin Mode := NewMode; QueryCloseCount := 0; ClosePageCount := 0; end;
                procedure AnswerQueryClose(): Boolean
                begin
                    QueryCloseCount += 1;
                    case Mode of
                        1: exit(false);
                        2: exit(Confirm('TCV close this page?'));
                    end;
                    exit(true);
                end;
                procedure NoteClosePage() begin ClosePageCount += 1; end;
                procedure QueryCloseCalls(): Integer begin exit(QueryCloseCount); end;
                procedure ClosePageCalls(): Integer begin exit(ClosePageCount); end;
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Card.Page.al"), """
            page 90471 "TCV Card"
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
                    Probe: Codeunit "TCV Probe";
                    MarkerVar: Text[10];
                trigger OnQueryClosePage(CloseAction: Action): Boolean
                begin
                    exit(Probe.AnswerQueryClose());
                end;
                trigger OnClosePage()
                begin
                    Probe.NoteClosePage();
                end;
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Tests.Codeunit.al"), """
            codeunit 90472 "TCV Tests"
            {
                Subtype = Test;
                var
                    Probe: Codeunit "TCV Probe";
                    ConfirmCalls: Integer;

                [Test]
                procedure AllowedClose_RunsOnClosePage()
                var
                    Card: TestPage "TCV Card";
                begin
                    Probe.Reset(0);
                    Card.OpenEdit();
                    Card.Close();
                    if Probe.QueryCloseCalls() <> 1 then Error('allowed: QueryClose %1', Probe.QueryCloseCalls());
                    if Probe.ClosePageCalls() <> 1 then Error('allowed: ClosePage %1', Probe.ClosePageCalls());
                end;

                [Test]
                procedure PlainVeto_CloseReturns()
                var
                    Card: TestPage "TCV Card";
                begin
                    Probe.Reset(1);
                    Card.OpenEdit();
                    Card.Close();
                    if Probe.QueryCloseCalls() <> 1 then Error('veto: QueryClose %1', Probe.QueryCloseCalls());
                    if Probe.ClosePageCalls() <> 0 then Error('veto: ClosePage %1', Probe.ClosePageCalls());
                end;

                [Test]
                [HandlerFunctions('ConfirmNo')]
                procedure ConfirmNoVeto_CloseReturns()
                var
                    Card: TestPage "TCV Card";
                begin
                    Probe.Reset(2);
                    ConfirmCalls := 0;
                    Card.OpenEdit();
                    Card.Close();
                    if ConfirmCalls <> 1 then Error('confirm: ConfirmCalls %1', ConfirmCalls);
                    if Probe.ClosePageCalls() <> 0 then Error('confirm: ClosePage %1', Probe.ClosePageCalls());
                end;

                [ConfirmHandler]
                procedure ConfirmNo(Question: Text[1024]; var Reply: Boolean)
                begin
                    ConfirmCalls += 1;
                    Reply := false;
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
