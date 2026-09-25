// TestPageFailedInsertRaisesTests — issue #4624. LiveNavTestPage.InsertPendingRow writes a
// started row with DataError.ThrowError, so a refused insert (a duplicate key) raises at the
// TestPage call that flushed the row instead of dropping the row silently. One AL test per flush
// route: insert on focus (ActivateControl) and Close() (FlushPendingNewRow), plus a contrast.
// The BC behaviour is adjudicated upstream by corpus codeunit 60045 "IPF Tests".
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageFailedInsertRaisesTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageFailedInsertRaisesTests()
    {
        _root = TestScratch.Dir("al-runner-testpage-failed-insert");
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
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "c7d1e2f3-4a5b-4c6d-9e8f-0a1b2c3d4624",
          "name": "Runner Mechanism - TestPage Failed Insert Raises",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62870, "to": 62879 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Objects.al"), """
        table 62870 "Fir Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Description; Text[100]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        page 62871 "Fir Card"
        {
            PageType = Card;
            SourceTable = "Fir Row";
            ApplicationArea = All;
            layout
            {
                area(Content)
                {
                    group(General)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                        field(Description; Rec.Description) { ApplicationArea = All; }
                    }
                }
            }
        }

        page 62872 "Fir Delayed Card"
        {
            PageType = Card;
            SourceTable = "Fir Row";
            DelayedInsert = true;
            ApplicationArea = All;
            layout
            {
                area(Content)
                {
                    group(General)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                        field(Description; Rec.Description) { ApplicationArea = All; }
                    }
                }
            }
        }

        codeunit 62873 "Fir Tests"
        {
            Subtype = Test;

            var
                Step: Text;

            local procedure Seed()
            var
                Row: Record "Fir Row";
            begin
                Row.DeleteAll();
                Row."No." := 'DUP';
                Row.Description := 'orig';
                Row.Insert();
                Step := '';
            end;

            local procedure Check(Expected: Text)
            var
                Actual: Text;
            begin
                Actual := GetLastErrorText();
                if StrPos(Actual, 'already exists') > 0 then
                    Actual := 'already exists';
                Actual := Step + '|' + Actual;
                if Actual <> Expected then
                    Error('observed %1, expected %2', Actual, Expected);
            end;

            // ActivateControl's insert on focus: the non-key write raises.
            [Test]
            procedure InsertOnFocus_DuplicateKey_RaisesAtTheWrite()
            begin
                Seed();
                asserterror DriveCard();
                Check('Description|already exists');
            end;

            // FlushPendingNewRow's insert: Close() raises.
            [Test]
            procedure DelayedClose_DuplicateKey_RaisesAtClose()
            begin
                Seed();
                asserterror DriveDelayedCard('DUP');
                Check('Close|already exists');
            end;

            // Contrast: a free key is written, and nothing raises before the sentinel.
            [Test]
            procedure DelayedClose_FreeKey_WritesTheRow()
            var
                Row: Record "Fir Row";
                Card: TestPage "Fir Delayed Card";
            begin
                Seed();
                Card.OpenNew();
                Card."No.".SetValue('NEW1');
                Card.Description.SetValue('typed');
                Card.Close();
                Row.Get('NEW1');
                if Row.Description <> 'typed' then
                    Error('NEW1 Description %1, expected typed', Row.Description);
            end;

            local procedure DriveCard()
            var
                Card: TestPage "Fir Card";
            begin
                Card.OpenNew();
                Step := 'No.';
                Card."No.".SetValue('DUP');
                Step := 'Description';
                Card.Description.SetValue('typed');
                Step := 'Close';
                Card.Close();
                Step := 'completed';
                Error('NO-ERROR-RAISED');
            end;

            local procedure DriveDelayedCard(No: Code[20])
            var
                Card: TestPage "Fir Delayed Card";
            begin
                Card.OpenNew();
                Step := 'No.';
                Card."No.".SetValue(No);
                Step := 'Description';
                Card.Description.SetValue('typed');
                Step := 'Close';
                Card.Close();
                Step := 'completed';
                Error('NO-ERROR-RAISED');
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

    [SkippableFact]
    public void AFailedPageInsert_RaisesAtTheCallThatFlushedIt()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        foreach (var name in new[]
                 {
                     "InsertOnFocus_DuplicateKey_RaisesAtTheWrite",
                     "DelayedClose_DuplicateKey_RaisesAtClose",
                     "DelayedClose_FreeKey_WritesTheRow",
                 })
            Assert.Contains("PASS  Codeunit62873." + name, output);
        Assert.DoesNotContain("FAIL", output);
    }
}
