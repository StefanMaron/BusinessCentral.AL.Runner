// TestPageInsertOnFocusTests — issue #4062. Pins the four rules LiveNavTestPage.ActivateControl
// applies when it decides a started new row is written, one AL test per rule, so removing any
// one rule reds exactly one test. The BC behaviour itself is adjudicated upstream by corpus
// codeunit 60576 "TPBK Tests"; see docs/testpage-write-buffer.md#insert-on-focus.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageInsertOnFocusTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageInsertOnFocusTests()
    {
        _root = TestScratch.Dir("al-runner-testpage-insert-on-focus");
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
          "id": "b1c2d3e4-5f60-4a7b-8c9d-0e1f2a3b4c62",
          "name": "Runner Mechanism - TestPage Insert On Focus",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62790, "to": 62799 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Objects.al"), """
        table 62790 "Iof Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Description; Text[100]) { }
                field(3; Note; Text[100]) { }
                field(4; "Desc At Insert"; Text[100]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }

            trigger OnInsert()
            var
                Existing: Record "Iof Row";
            begin
                if "No." = '' then
                    "No." := 'AUTO' + Format(Existing.Count() + 1);
                "Desc At Insert" := CopyStr('[' + Description + ']', 1, 100);
            end;
        }

        page 62791 "Iof Card"
        {
            PageType = Card;
            SourceTable = "Iof Row";
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

        page 62792 "Iof Delayed Card"
        {
            PageType = Card;
            SourceTable = "Iof Row";
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

        page 62793 "Iof List"
        {
            PageType = List;
            SourceTable = "Iof Row";
            ApplicationArea = All;
            layout
            {
                area(Content)
                {
                    repeater(Rows)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                        field(Description; Rec.Description) { ApplicationArea = All; }
                        field(Note; Rec.Note) { ApplicationArea = All; }
                    }
                }
            }
        }

        page 62795 "Iof Key Locked Card"
        {
            PageType = Card;
            SourceTable = "Iof Row";
            ApplicationArea = All;
            layout
            {
                area(Content)
                {
                    group(General)
                    {
                        field(Description; Rec.Description) { ApplicationArea = All; }
                        field("No."; Rec."No.") { ApplicationArea = All; Editable = false; }
                    }
                }
            }
        }

        codeunit 62794 "Iof Tests"
        {
            Subtype = Test;

            // Rule 1: focus moving to a non-key control inserts, before that control's value is written.
            [Test]
            procedure NonKeyWrite_InsertsBeforeTheWrite()
            var
                Row: Record "Iof Row";
                Card: TestPage "Iof Card";
            begin
                Row.DeleteAll();
                Card.OpenNew();
                Card.Description.SetValue('x');
                if Row.Count() <> 1 then
                    Error('rows before Close: %1, expected 1', Row.Count());
                Row.FindFirst();
                if (Row."No." <> 'AUTO1') or (Row."Desc At Insert" <> '[]') then
                    Error('No.=%1, Description seen by OnInsert=%2; expected AUTO1 and []', Row."No.", Row."Desc At Insert");
                Card.Close();
            end;

            // Rule 2: focus moving to a KEY control does not insert. "No." is not editable here, so
            // the initial control is Description and the move to "No." leaves a field control.
            [Test]
            procedure KeyActivation_DoesNotInsert()
            var
                Row: Record "Iof Row";
                Card: TestPage "Iof Key Locked Card";
            begin
                Row.DeleteAll();
                Card.OpenNew();
                Card."No.".Activate();
                if not Row.IsEmpty() then
                    Error('focus on the key control inserted a row before Close');
                Card.Close();
            end;

            // Rule 3: DelayedInsert = true never inserts on focus.
            [Test]
            procedure DelayedInsert_DoesNotInsertOnFocus()
            var
                Row: Record "Iof Row";
                Card: TestPage "Iof Delayed Card";
            begin
                Row.DeleteAll();
                Card.OpenNew();
                Card.Description.SetValue('x');
                if not Row.IsEmpty() then
                    Error('a DelayedInsert page inserted before Close');
                Card.Close();
                if Row.Count() <> 1 then
                    Error('rows after Close: %1, expected 1', Row.Count());
            end;

            // Rule 4: on a repeater, focus arriving from another row does not insert. After New(),
            // Description already has focus (on the old row), so its write moves nothing; Note takes
            // focus from the old row (no insert); Description then takes it from Note on the new row.
            [Test]
            procedure Repeater_FocusFromAnotherRow_DoesNotInsert()
            var
                Row: Record "Iof Row";
                Rows: TestPage "Iof List";
            begin
                Row.DeleteAll();
                Rows.OpenNew();
                Rows.Description.SetValue('a');
                Rows.New();
                Rows.Description.SetValue('b');
                Rows.Note.SetValue('c');
                if Row.Count() <> 1 then
                    Error('rows after focus came from the other row: %1, expected 1', Row.Count());
                Rows.Description.SetValue('d');
                if Row.Count() <> 2 then
                    Error('rows after focus moved within the new row: %1, expected 2', Row.Count());
                Rows.Close();
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
    public void EachInsertOnFocusRule_HoldsOnItsOwnTest()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var (output, exit) = RunBundled();

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        foreach (var name in new[]
                 {
                     "NonKeyWrite_InsertsBeforeTheWrite",
                     "KeyActivation_DoesNotInsert",
                     "DelayedInsert_DoesNotInsertOnFocus",
                     "Repeater_FocusFromAnotherRow_DoesNotInsert",
                 })
            Assert.Contains("PASS  Codeunit62794." + name, output);
        Assert.DoesNotContain("FAIL", output);
    }
}
