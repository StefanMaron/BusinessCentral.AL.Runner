// TestPagePartAdoptedFromHostTests — issue #2201 (TestPage.<part> and the host's own
// CurrPage.<part>.Page used to be two DIFFERENT NavForm instances for the same subpage
// control).
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it proves that OUR
// OWN dispatch path — MockTestPage.GetPart via RunnerPageInstance.AdoptFromHost — reaches the
// SAME NavForm the host's own compiled AL gets back from CurrPage.<part>, rather than building
// a second, disconnected instance the way TestPageFactory.TryBuild/TryBuildRecordless alone
// used to. The BEHAVIORAL claim ("a SourceTableTemporary part's rows, pushed from the host's
// own OnOpenPage, must be visible to both the host's own AL and the TestPage that later reads
// it, on real BC") is proven upstream against a live BC service tier — see
// StefanMaron/BusinessCentral.AL.Language.Tests, "TP SrcTemp Tests" (codeunit 60807), per
// .claude/rules/bc-behavior-tests-go-upstream.md. This test exists so a regression in OUR OWN
// instance-adoption mechanism fails loudly here, without needing the corpus's al-language
// submodule pin bumped first.
//
// RED/GREEN proof: reverting RunnerPageInstance.AdoptFromHost to always return null (the
// pre-fix shape, where MockTestPage.GetPart falls straight through to
// TestPageFactory.TryBuild/TryBuildRecordless) makes
// HostSeededTemporaryPart_TestPageSeesTheSameRow fail: TestPage.Lines.First() finds no row,
// because the disconnected instance TryBuild constructs never received the row the host's
// OnOpenPage pushed into the ADOPTED instance's own temporary Rec.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPagePartAdoptedFromHostTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPagePartAdoptedFromHostTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-testpage-part-adopted-from-host", Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// A ListPart declaring SourceTableTemporary, hosted on a Card page that pushes one row
    /// into it from OnOpenPage — before the TestPage side ever touches the part. The part's
    /// own temporary Rec only exists on whichever instance the host wrote through; a
    /// disconnected second instance has an EMPTY table, not merely a stale value.
    /// </summary>
    private void WriteBundle()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "d2e3f4a5-6b7c-4890-9d0e-1f2a3b4c5d02",
          "name": "Runner Mechanism - TestPage Part Adopted From Host",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62410, "to": 62412 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpahRow.Table.al"), """
        table 62410 "Tpah Row"
        {
            DataClassification = CustomerContent;

            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; Name; Text[50]) { }
            }

            keys
            {
                key(PK; "Entry No.") { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpahPart.Page.al"), """
        page 62411 "Tpah Part"
        {
            PageType = ListPart;
            SourceTable = "Tpah Row";
            SourceTableTemporary = true;
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    repeater(Rows)
                    {
                        field(Name; Rec.Name)
                        {
                            ApplicationArea = All;
                        }
                    }
                }
            }

            internal procedure SetRows(var TempRow: Record "Tpah Row" temporary)
            begin
                Rec.DeleteAll();
                if TempRow.FindSet() then
                    repeat
                        Rec := TempRow;
                        Rec.Insert();
                    until TempRow.Next() = 0;
            end;
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpahHost.Page.al"), """
        page 62412 "Tpah Host"
        {
            PageType = Card;
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    part(Lines; "Tpah Part")
                    {
                        ApplicationArea = All;
                    }
                }
            }

            trigger OnOpenPage()
            var
                TempRow: Record "Tpah Row" temporary;
            begin
                TempRow."Entry No." := 1;
                TempRow.Name := 'FROM-HOST';
                TempRow.Insert();
                CurrPage.Lines.Page.SetRows(TempRow);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(_root, "TpahTests.Codeunit.al"), """
        codeunit 62409 "Tpah Tests"
        {
            Subtype = Test;

            [Test]
            procedure HostSeededTemporaryPart_TestPageSeesTheSameRow()
            var
                Host: TestPage "Tpah Host";
            begin
                Host.OpenEdit();

                if not Host.Lines.First() then
                    Error('TestPage.Lines must show the row the host inserted from OnOpenPage — the part page instance must be the SAME one the host wrote through');
                if Host.Lines.Name.Value() <> 'FROM-HOST' then
                    Error('the row value must be what the host wrote, got: ' + Host.Lines.Name.Value());

                Host.Close();
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
    /// Positive: the TestPage's own read must see the row through the SAME part page instance
    /// the host's OnOpenPage wrote through, not a disconnected second one seeded from nothing.
    /// </summary>
    [SkippableFact]
    public void HostSeededTemporaryPart_TestPageSeesTheSameRow()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62409.HostSeededTemporaryPart_TestPageSeesTheSameRow", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
