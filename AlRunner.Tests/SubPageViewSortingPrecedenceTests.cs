// SubPageViewSortingPrecedenceTests: #4969. A part control's SubPageView sorting is applied
// BEFORE the part page's own SourceTableView and OnOpenPage, so each can override it. The
// BC-observable claim is measured upstream by corpus codeunit 67950 "SPO Tests"
// (StefanMaron/BusinessCentral.AL.Language.Tests#507); this pins the runner's own ordering in
// RunnerPageInstance.RaiseOnOpenPage, spawning the real runner against a synthetic bundle.
// No Library Assert dependency (.claude/rules/no-base-app-in-csharp-tests.md): each AL test
// raises Error() with the order it saw.
//
// Seeded rows, all three orders distinct and so are their reverses:
//   Entry  Rank  Score      PK 1,2,3,4 | Rank 2,4,1,3 / desc 3,1,4,2 | Score 4,1,3,2 / desc 2,3,1,4
//     1     30    200
//     2     10    400
//     3     40    300
//     4     20    100
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public class SubPageViewSortingPrecedenceTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --show-pass \"").Append(bundle).Append('"');
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
        var root = TestScratch.Dir("al-runner-subpageview-sorting-precedence-4969");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c4969000-0000-4000-8000-000000004969",
          "name": "SubPageViewSortingPrecedence4969",
          "publisher": "Repro4969",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62960, "to": 62969 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "Objects.al"), """
        table 62960 "Spp Row"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; Rank; Integer) { }
                field(3; Score; Integer) { }
            }
            keys
            {
                key(PK; "Entry No.") { Clustered = true; }
                key(ByRank; Rank) { }
                key(ByScore; Score) { }
            }
        }

        page 62961 "Spp Plain Part"
        {
            PageType = ListPart;
            SourceTable = "Spp Row";
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    repeater(Lines)
                    {
                        field("Entry No."; Rec."Entry No.") { ApplicationArea = All; }
                    }
                }
            }
        }
        page 62962 "Spp View Part"
        {
            PageType = ListPart;
            SourceTable = "Spp Row";
            ApplicationArea = All;
            SourceTableView = sorting(Score);

            layout
            {
                area(Content)
                {
                    repeater(Lines)
                    {
                        field("Entry No."; Rec."Entry No.") { ApplicationArea = All; }
                    }
                }
            }
        }
        page 62963 "Spp Key Part"
        {
            PageType = ListPart;
            SourceTable = "Spp Row";
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    repeater(Lines)
                    {
                        field("Entry No."; Rec."Entry No.") { ApplicationArea = All; }
                    }
                }
            }

            trigger OnOpenPage()
            begin
                Rec.SetCurrentKey(Score);
            end;
        }
        page 62964 "Spp Desc Part"
        {
            PageType = ListPart;
            SourceTable = "Spp Row";
            ApplicationArea = All;

            layout
            {
                area(Content)
                {
                    repeater(Lines)
                    {
                        field("Entry No."; Rec."Entry No.") { ApplicationArea = All; }
                    }
                }
            }

            trigger OnOpenPage()
            begin
                Rec.Ascending(false);
            end;
        }

        page 62965 "Spp Host"
        {
            PageType = Card;
            SourceTable = "Spp Row";
            ApplicationArea = All;
            UsageCategory = None;

            layout
            {
                area(Content)
                {
                    field("Entry No."; Rec."Entry No.") { ApplicationArea = All; }
                    part(PlainRank; "Spp Plain Part")
                    {
                        ApplicationArea = All;
                        SubPageView = sorting(Rank);
                    }
                    part(ViewRankDesc; "Spp View Part")
                    {
                        ApplicationArea = All;
                        SubPageView = sorting(Rank) order(descending);
                    }
                    part(KeyRank; "Spp Key Part")
                    {
                        ApplicationArea = All;
                        SubPageView = sorting(Rank);
                    }
                    part(DescRank; "Spp Desc Part")
                    {
                        ApplicationArea = All;
                        SubPageView = sorting(Rank);
                    }
                }
            }
        }

        codeunit 62966 "Spp Tests"
        {
            Subtype = Test;

            local procedure AddRow(EntryNo: Integer; NewRank: Integer; NewScore: Integer)
            var
                Row: Record "Spp Row";
            begin
                Row.Init();
                Row."Entry No." := EntryNo;
                Row.Rank := NewRank;
                Row.Score := NewScore;
                Row.Insert();
            end;

            local procedure OpenHost(var Host: TestPage "Spp Host")
            var
                Row: Record "Spp Row";
            begin
                Row.DeleteAll();
                AddRow(1, 30, 200);
                AddRow(2, 10, 400);
                AddRow(3, 40, 300);
                AddRow(4, 20, 100);
                Host.OpenEdit();
                Host.GoToKey(1);
            end;

            local procedure Assert(Condition: Boolean; Msg: Text)
            begin
                if not Condition then
                    Error(Msg);
            end;

            [Test]
            procedure ViewOnly_OrdersByTheViewKey()
            var
                Host: TestPage "Spp Host";
                Seq: Text;
            begin
                OpenHost(Host);
                Assert(Host.PlainRank.First(), 'PlainRank: no first row');
                Seq := Host.PlainRank."Entry No.".Value();
                Host.PlainRank.Next();
                Seq += ',' + Host.PlainRank."Entry No.".Value();
                Host.PlainRank.Next();
                Seq += ',' + Host.PlainRank."Entry No.".Value();
                Host.PlainRank.Next();
                Seq += ',' + Host.PlainRank."Entry No.".Value();
                Host.Close();
                if Seq <> '2,4,1,3' then
                    Error('a part with no order of its own takes the view key: expected 2,4,1,3, got %1', Seq);
            end;

            [Test]
            procedure PartViewKey_KeepsTheControlsDirection()
            var
                Host: TestPage "Spp Host";
                Seq: Text;
            begin
                OpenHost(Host);
                Assert(Host.ViewRankDesc.First(), 'ViewRankDesc: no first row');
                Seq := Host.ViewRankDesc."Entry No.".Value();
                Host.ViewRankDesc.Next();
                Seq += ',' + Host.ViewRankDesc."Entry No.".Value();
                Host.ViewRankDesc.Next();
                Seq += ',' + Host.ViewRankDesc."Entry No.".Value();
                Host.ViewRankDesc.Next();
                Seq += ',' + Host.ViewRankDesc."Entry No.".Value();
                Host.Close();
                if Seq <> '2,3,1,4' then
                    Error('the part SourceTableView key with the control view direction: expected 2,3,1,4, got %1', Seq);
            end;

            [Test]
            procedure PartOnOpenPageSetCurrentKey_Wins()
            var
                Host: TestPage "Spp Host";
                Seq: Text;
            begin
                OpenHost(Host);
                Assert(Host.KeyRank.First(), 'KeyRank: no first row');
                Seq := Host.KeyRank."Entry No.".Value();
                Host.KeyRank.Next();
                Seq += ',' + Host.KeyRank."Entry No.".Value();
                Host.KeyRank.Next();
                Seq += ',' + Host.KeyRank."Entry No.".Value();
                Host.KeyRank.Next();
                Seq += ',' + Host.KeyRank."Entry No.".Value();
                Host.Close();
                if Seq <> '4,1,3,2' then
                    Error('the part OnOpenPage key over the control view key: expected 4,1,3,2, got %1', Seq);
            end;

            [Test]
            procedure PartOnOpenPageAscendingFalse_ReversesTheViewKey()
            var
                Host: TestPage "Spp Host";
                Seq: Text;
            begin
                OpenHost(Host);
                Assert(Host.DescRank.First(), 'DescRank: no first row');
                Seq := Host.DescRank."Entry No.".Value();
                Host.DescRank.Next();
                Seq += ',' + Host.DescRank."Entry No.".Value();
                Host.DescRank.Next();
                Seq += ',' + Host.DescRank."Entry No.".Value();
                Host.DescRank.Next();
                Seq += ',' + Host.DescRank."Entry No.".Value();
                Host.Close();
                if Seq <> '3,1,4,2' then
                    Error('the control view key reversed by the part OnOpenPage: expected 3,1,4,2, got %1', Seq);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void PartOwnOrder_OverridesTheControlsSubPageViewSorting()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(WriteBundle());

        Assert.True(exit == 0, $"Expected the bundle to pass; exit={exit}\n{output}");
        Assert.Contains("PASS  Codeunit62966.ViewOnly_OrdersByTheViewKey", output);
        Assert.Contains("PASS  Codeunit62966.PartViewKey_KeepsTheControlsDirection", output);
        Assert.Contains("PASS  Codeunit62966.PartOnOpenPageSetCurrentKey_Wins", output);
        Assert.Contains("PASS  Codeunit62966.PartOnOpenPageAscendingFalse_ReversesTheViewKey", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
