using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for issue #4623: a part whose row was loaded FOR it (host open, host
/// GoToKey) rather than reached by its own navigation takes its first Next() onto the row it
/// already shows — LiveNavTestPage._unpositionedAt, set by LiveNavTestPart.ReloadLinkedRow.
/// The BC-behaviour claim lives upstream in corpus codeunit 60229 "OKP Part Next Tests"; this
/// pins the runner's own flag, including that First() clears it.
/// </summary>
public class TestPagePartUnpositionedNextTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static readonly Lazy<(string output, int exit)> Run = new(() => RunRunner(WriteBundle()));

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" \"").Append(bundle).Append('"');
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
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static string WriteBundle()
    {
        var root = TestScratch.Dir("al-runner-testpage-part-unpositioned-next-4623");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c4623000-0000-4000-8000-000000004623",
          "name": "TestPagePartUnpositionedNext4623",
          "publisher": "Repro4623",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 64623, "to": 64630 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "Objects.al"), """
        table 64623 "Upn Header"
        {
            DataClassification = CustomerContent;
            fields { field(1; "Code"; Code[20]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        table 64624 "Upn Line"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Header Code"; Code[20]) { }
                field(2; "Line No."; Integer) { }
                field(3; Reference; Text[30]) { }
            }
            keys { key(PK; "Header Code", "Line No.") { Clustered = true; } }
        }

        page 64625 "Upn Lines"
        {
            PageType = ListPart;
            SourceTable = "Upn Line";
            ApplicationArea = All;
            layout
            {
                area(Content)
                {
                    repeater(Rows) { field(Reference; Rec.Reference) { ApplicationArea = All; } }
                }
            }
        }

        page 64626 "Upn Card"
        {
            PageType = Card;
            SourceTable = "Upn Header";
            ApplicationArea = All;
            UsageCategory = None;
            layout
            {
                area(Content)
                {
                    field("Code"; Rec."Code") { ApplicationArea = All; }
                    part(Lines; "Upn Lines")
                    {
                        ApplicationArea = All;
                        SubPageLink = "Header Code" = field("Code");
                    }
                }
            }
        }

        codeunit 64627 "Upn Tests"
        {
            Subtype = Test;

            local procedure Initialize()
            var
                Header: Record "Upn Header";
                Line: Record "Upn Line";
            begin
                Line.DeleteAll();
                Header.DeleteAll();
                AddHeader('H1');
                AddLine('H1', 10000, 'H1-FIRST');
                AddLine('H1', 20000, 'H1-SECOND');
                AddHeader('H2');
                AddLine('H2', 10000, 'H2-FIRST');
                AddLine('H2', 20000, 'H2-SECOND');
            end;

            local procedure AddHeader(HeaderCode: Code[20])
            var
                Header: Record "Upn Header";
            begin
                Header."Code" := HeaderCode;
                Header.Insert();
            end;

            local procedure AddLine(HeaderCode: Code[20]; LineNo: Integer; Reference: Text[30])
            var
                Line: Record "Upn Line";
            begin
                Line."Header Code" := HeaderCode;
                Line."Line No." := LineNo;
                Line.Reference := Reference;
                Line.Insert();
            end;

            [Test]
            procedure GotoKeyThenNext_LandsOnFirstRow()
            var
                Card: TestPage "Upn Card";
                Got: Text;
            begin
                Initialize();
                Card.OpenEdit();
                Card.GoToKey('H2');
                if not Card.Lines.Next() then Error('first Next() answered false');
                Got := Card.Lines.Reference.Value();
                if Got <> 'H2-FIRST' then Error('first Next() after GoToKey landed on %1', Got);
                if not Card.Lines.Next() then Error('second Next() answered false');
                Got := Card.Lines.Reference.Value();
                if Got <> 'H2-SECOND' then Error('second Next() after GoToKey landed on %1', Got);
                Card.Close();
            end;

            [Test]
            procedure FirstThenNext_LandsOnSecondRow()
            var
                Card: TestPage "Upn Card";
                Got: Text;
            begin
                Initialize();
                Card.OpenEdit();
                Card.GoToKey('H2');
                Card.Lines.First();
                if not Card.Lines.Next() then Error('Next() after First() answered false');
                Got := Card.Lines.Reference.Value();
                if Got <> 'H2-SECOND' then Error('Next() after First() landed on %1', Got);
                Card.Close();
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void GotoKeyThenNext_LandsOnFirstRow()
    {
        TestArtifacts.SkipIfMissing();
        var (output, exit) = Run.Value;
        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit64627.GotoKeyThenNext_LandsOnFirstRow", output);
    }

    [SkippableFact]
    public void FirstThenNext_LandsOnSecondRow()
    {
        TestArtifacts.SkipIfMissing();
        var (output, exit) = Run.Value;
        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit64627.FirstThenNext_LandsOnSecondRow", output);
    }
}
