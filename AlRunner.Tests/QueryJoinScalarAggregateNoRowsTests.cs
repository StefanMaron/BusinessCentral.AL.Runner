using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #5436 — runner-mechanism guard for the JOIN path's scalar aggregate (a query with only
/// aggregated columns): when the query's WHERE filters leave no joined row, the one defaulted
/// row is still returned, and the HAVING pass (RecordPatches.ApplyJoinRuntimeFilters) does not
/// re-evaluate a WHERE condition against that row's null slots
/// (RecordPatches.IsAppliedBeforeGrouping).
///
/// The BC-behaviour claim lives upstream in corpus codeunit 69980 (query/
/// TestQueryDataItemTableFilterDatabaseConst.al). This class pins the runner's own pipeline, with
/// two controls so a pass cannot come from skipping every filter: a filter that selects rows
/// still sums only those rows, and a HAVING condition on the aggregated column still drops the
/// row it must.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryJoinScalarAggregateNoRowsTests
{
    private static string WriteBundle()
    {
        var root = TestScratch.Dir("al-runner-query-join-scalar-no-rows-5436");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c1f0a5e2-5436-4d7b-9a61-000000005436",
          "name": "QJS 5436 Repro",
          "publisher": "Repro5436",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 75360, "to": 75370 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Qjs5436.al"), """
        table 75360 "QJS5436 Entry"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; Positive; Boolean) { }
                field(3; "Item No."; Code[20]) { }
                field(4; Qty; Decimal) { }
            }
            keys { key(PK; "Entry No.", Positive) { Clustered = true; } }
        }

        query 75361 "QJS5436 Scalar"
        {
            QueryType = Normal;
            elements
            {
                dataitem(ForSide; "QJS5436 Entry")
                {
                    DataItemTableFilter = Positive = const(false);
                    filter(ItemNo; "Item No.") { }
                    column(TotalQty; Qty) { Method = Sum; }
                    dataitem(FromSide; "QJS5436 Entry")
                    {
                        DataItemLink = "Entry No." = ForSide."Entry No.";
                        SqlJoinType = InnerJoin;
                        DataItemTableFilter = Positive = const(true);
                    }
                }
            }
        }

        codeunit 75362 "QJS5436 Tests"
        {
            Subtype = Test;

            local procedure Init()
            var
                Entry: Record "QJS5436 Entry";
            begin
                Entry.DeleteAll();
                InsertPair(1, 'I-1', 10);
                InsertPair(2, 'I-1', 20);
                InsertPair(3, 'I-2', 40);
            end;

            local procedure InsertPair(EntryNo: Integer; ItemNo: Code[20]; Quantity: Decimal)
            var
                Entry: Record "QJS5436 Entry";
            begin
                Entry.Init(); Entry."Entry No." := EntryNo; Entry.Positive := false; Entry."Item No." := ItemNo; Entry.Qty := Quantity; Entry.Insert();
                Entry.Init(); Entry."Entry No." := EntryNo; Entry.Positive := true; Entry."Item No." := ItemNo; Entry.Qty := Quantity; Entry.Insert();
            end;

            local procedure ReadRows(var Q: Query "QJS5436 Scalar"): Text
            var
                Rows: Text;
            begin
                Q.Open();
                while Q.Read() do
                    Rows += Format(Q.TotalQty, 0, 9) + ';';
                Q.Close();
                exit(Rows);
            end;

            [Test]
            procedure FilterSelectingNoJoinedRow_StillReadsOneZeroRow()
            var
                Q: Query "QJS5436 Scalar";
                Rows: Text;
            begin
                Init();
                Q.SetRange(ItemNo, 'I-NONE');
                Rows := ReadRows(Q);
                if Rows <> '0;' then
                    Error('expected the one defaulted row 0; got %1', Rows);
            end;

            [Test]
            procedure FilterSelectingRows_SumsOnlyThoseRows()
            var
                Q: Query "QJS5436 Scalar";
                Rows: Text;
            begin
                Init();
                Q.SetRange(ItemNo, 'I-1');
                Rows := ReadRows(Q);
                if Rows <> '30;' then
                    Error('expected only I-1 summed to 30; got %1', Rows);
            end;

            [Test]
            procedure HavingOnTheAggregatedColumn_StillDecidesWhetherTheRowIsKept()
            var
                Q: Query "QJS5436 Scalar";
                Rows: Text;
            begin
                Init();
                Q.SetFilter(TotalQty, '>100');
                Rows := ReadRows(Q);
                if Rows <> '' then
                    Error('expected no row for a sum of 70 against > 100; got %1', Rows);

                Clear(Q);
                Q.SetFilter(TotalQty, '>50');
                Rows := ReadRows(Q);
                if Rows <> '70;' then
                    Error('expected the row 70 against > 50; got %1', Rows);
            end;

            [Test]
            procedure NoFilter_SumsEveryRow()
            var
                Q: Query "QJS5436 Scalar";
                Rows: Text;
            begin
                Init();
                Rows := ReadRows(Q);
                if Rows <> '70;' then
                    Error('expected every pair summed to 70; got %1', Rows);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public async Task ScalarAggregateJoin_WhereFilterLeavingNoRow_ReturnsTheDefaultedRow()
    {
        TestArtifacts.SkipIfMissing();

        var result = await SuiteServer.RunViaServer(WriteBundle());

        Assert.Empty(result.CompilationErrors);
        Assert.True(result.ExitCode == 0, $"runner exit {result.ExitCode}:\n{result}");
        result.AssertCounts(passed: 4, failed: 0, errors: 0);
    }
}
