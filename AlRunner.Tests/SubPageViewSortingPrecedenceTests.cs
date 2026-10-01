// SubPageViewSortingPrecedenceTests: #4969. A part control's SubPageView sorting is applied
// BEFORE the part page's own SourceTableView and OnOpenPage, so each can override it. The
// BC-observable claim is measured upstream by corpus codeunit 67950 "SPO Tests"
// (StefanMaron/BusinessCentral.AL.Language.Tests#507); this pins the runner's own ordering in
// RunnerPageInstance.RaiseOnOpenPage, running the real runner (SuiteServer) against a synthetic bundle.
// No Library Assert dependency (.claude/rules/no-base-app-in-csharp-tests.md): each AL test
// raises Error() with the order it saw.
//
// Seeded rows, all three orders distinct and so are their reverses:
//   Entry  Rank  Score      PK 1,2,3,4 | Rank 2,4,1,3 / desc 3,1,4,2 | Score 4,1,3,2 / desc 2,3,1,4
//     1     30    200
//     2     10    400
//     3     40    300
//     4     20    100
using Xunit;

namespace AlRunner.Tests;

public class SubPageViewSortingPrecedenceTests
{
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
    public async Task PartOwnOrder_OverridesTheControlsSubPageViewSorting()
    {
        TestArtifacts.SkipIfMissing();

        var r = await SuiteServer.RunViaServer(WriteBundle());

        Assert.True(r.ExitCode == 0, $"Expected the bundle to pass; exit={r.ExitCode}\n{r}");
        r.AssertPassed("Codeunit62966.ViewOnly_OrdersByTheViewKey");
        r.AssertPassed("Codeunit62966.PartViewKey_KeepsTheControlsDirection");
        r.AssertPassed("Codeunit62966.PartOnOpenPageSetCurrentKey_Wins");
        r.AssertPassed("Codeunit62966.PartOnOpenPageAscendingFalse_ReversesTheViewKey");
        r.AssertNoFailures();
    }
}
