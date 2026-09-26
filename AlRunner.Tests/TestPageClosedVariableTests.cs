// TestPageClosedVariableTests — issue #4713.
//
// Runner mechanism: NavTestPageBase.Close() detaches the TestPage variable (BC's InternalClear sets
// testPage = null), and the runner stands in for that with LiveNavTestPage's detach flag, read by
// the rewritten CheckPageOpened. What BC does after a Close() is measured upstream by corpus
// codeunit 60419 "QCV Close Veto Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#431); this
// fixture pins the runner's two halves of it: the detach fires after an allowed close and after a
// refused one, and reopening the same variable clears it.
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
    public void AClosedTestPage_IsNotOpen_UntilItIsOpenedAgain()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        var m = Regex.Match(output, @"Tests:\s+(\d+)\s+passed\s+(\d+)\s+failed\s+(\d+)");
        Assert.True(m.Success, $"no Tests: summary line; exit={exit}\n{output}");
        Assert.True(m.Groups[1].Value == "5" && m.Groups[2].Value == "5" && m.Groups[3].Value == "0",
            $"expected all five arms to pass; exit={exit}\n{output}");
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

        File.WriteAllText(Path.Combine(_root, "Tests.Codeunit.al"), """
            codeunit 90715 "TPCV Tests"
            {
                Subtype = Test;
                var
                    Probe: Codeunit "TPCV Probe";

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
