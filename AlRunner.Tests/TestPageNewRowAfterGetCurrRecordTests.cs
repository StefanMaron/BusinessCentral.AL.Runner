// TestPageNewRowAfterGetCurrRecordTests — issue #2394. Pins the runner mechanism behind
// LiveNavTestPage.NewRowBecameCurrent: a top-level page's new row raises OnAfterGetCurrRecord
// alone (not OnAfterGetRecord), and a row that trigger handed the page from the table is adopted
// as an existing row, so the next write modifies it instead of inserting a second one. The BC
// behaviour is adjudicated upstream by corpus codeunit 60927 "ONG Tests"; see
// docs/testpage-write-buffer.md#the-new-row-becomes-current.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageNewRowAfterGetCurrRecordTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageNewRowAfterGetCurrRecordTests()
    {
        _root = TestScratch.Dir("al-runner-testpage-newrow-agcr");
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
          "id": "b1c2d3e4-5f60-4a7b-8c9d-0e1f2a3b2394",
          "name": "Runner Mechanism - TestPage New Row AfterGetCurrRecord",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62850, "to": 62859 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Objects.al"), """
        table 62850 "Nrc Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Name; Text[50]) { }
                field(3; Templated; Boolean) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        table 62851 "Nrc Log"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Key"; Code[20]) { }
                field(2; Hits; Integer) { }
            }
            keys { key(PK; "Key") { Clustered = true; } }

            procedure Bump(Name: Code[20])
            var
                Log: Record "Nrc Log";
            begin
                if Log.Get(Name) then begin
                    Log.Hits += 1;
                    Log.Modify();
                end else begin
                    Log."Key" := Name;
                    Log.Hits := 1;
                    Log.Insert();
                end;
            end;
        }

        page 62852 "Nrc Card"
        {
            PageType = Card;
            SourceTable = "Nrc Row";
            ApplicationArea = All;
            layout
            {
                area(Content)
                {
                    group(General)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                        field(Name; Rec.Name) { ApplicationArea = All; }
                    }
                }
            }

            var
                NewMode: Boolean;

            trigger OnNewRecord(BelowxRec: Boolean)
            begin
                NewMode := true;
            end;

            trigger OnAfterGetRecord()
            var
                Log: Record "Nrc Log";
            begin
                Log.Bump('AGR');
            end;

            trigger OnAfterGetCurrRecord()
            var
                Row: Record "Nrc Row";
                Log: Record "Nrc Log";
            begin
                Log.Bump('AGCR');
                if not NewMode then
                    exit;
                NewMode := false;
                Row."No." := 'T1';
                Row.Templated := true;
                Row.Insert();
                Rec.Copy(Row);
            end;
        }

        codeunit 62853 "Nrc Tests"
        {
            Subtype = Test;

            // The new row raises OnAfterGetCurrRecord and not OnAfterGetRecord: nothing was fetched.
            [Test]
            procedure OpenNew_RaisesAfterGetCurrRecordOnly()
            var
                Log: Record "Nrc Log";
                Card: TestPage "Nrc Card";
            begin
                Reset();
                Card.OpenNew();
                if not Log.Get('AGCR') then
                    Error('OnAfterGetCurrRecord did not run on OpenNew');
                if Log.Get('AGR') then
                    Error('OnAfterGetRecord ran for a new row: %1 time(s)', Log.Hits);
                Card.Close();
            end;

            // The trigger handed the page an existing row: typing modifies it, no second insert.
            [Test]
            procedure RowHandedOverByTheTrigger_IsModifiedNotInsertedAgain()
            var
                Row: Record "Nrc Row";
                Card: TestPage "Nrc Card";
            begin
                Reset();
                Card.OpenNew();
                Card.Name.SetValue('typed');
                Card.OK().Invoke();
                if Row.Count() <> 1 then
                    Error('rows: %1, expected 1', Row.Count());
                Row.Get('T1');
                if Row.Name <> 'typed' then
                    Error('Name on T1 was <%1>, expected <typed>', Row.Name);
            end;

            local procedure Reset()
            var
                Row: Record "Nrc Row";
                Log: Record "Nrc Log";
            begin
                Row.DeleteAll();
                Log.DeleteAll();
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
    public void NewRow_RaisesAfterGetCurrRecord_AndAdoptsTheRowItHandsOver()
    {
        TestArtifacts.SkipIfMissing();
        WriteBundle();
        var (output, exit) = RunBundled();
        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        foreach (var name in new[]
                 {
                     "OpenNew_RaisesAfterGetCurrRecordOnly",
                     "RowHandedOverByTheTrigger_IsModifiedNotInsertedAgain",
                 })
            Assert.Contains("PASS  Codeunit62853." + name, output);
        Assert.DoesNotContain("FAIL", output);
    }
}
