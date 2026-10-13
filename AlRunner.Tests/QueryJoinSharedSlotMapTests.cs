using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #3523 -- a multi-dataitem JOIN query's post-join filters must read the column layout
/// the executor actually produced its rows with (AlRunner.QueryJoin.JoinExecutor's projection
/// plan), not a second copy of the slot-allocation algorithm. These bundles drive a runtime
/// SetRange/SetFilter through every column shape the plan allocates a slot for -- a normal
/// column, a filter-only element on each side of the join, an aggregated column (HAVING) and a
/// FlowField column riding the FlowField-calculation synthesized dataitem -- and always assert
/// rows being EXCLUDED, with concrete row values, never only an unfiltered happy path.
///
/// RUNNER-MECHANISM suite (which slot the filter layer reads), not a claim about BC: whether
/// BC filters a join this way is adjudicated by the al-language corpus (query/TestQueryJoin*.al).
/// Needs the BC artifact cache; skips when absent.
/// </summary>
public class QueryJoinSharedSlotMapTests
{
    private static string WriteBundle()
    {
        var root = TestScratch.Dir("al-runner-query-join-shared-slotmap-3523");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "QJSM 3523 Repro",
          "publisher": "Repro3523",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 62530, "to": 62549 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Qjsm.al"), """
        table 62530 "Qjsm Header"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Region; Integer) { }
                field(3; "Total Amount"; Decimal)
                {
                    FieldClass = FlowField;
                    CalcFormula = sum("Qjsm Line".Amount where("Header No." = field("No.")));
                }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }
        table 62531 "Qjsm Line"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; "Header No."; Code[20]) { }
                field(3; Amount; Decimal) { }
                field(4; Kind; Integer) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        // Filter-only elements on BOTH dataitems (RegionFilter, KindFilter), a normal column
        // (HeaderNo) and an aggregated one (LineTotal): the plan gives the two filter-only
        // columns dedicated extra slots past the projected ones, one per dataitem.
        query 62532 "QJSM Join Filter Only"
        {
            QueryType = Normal;
            OrderBy = ascending(HeaderNo);
            elements
            {
                dataitem(Header; "Qjsm Header")
                {
                    column(HeaderNo; "No.") { }
                    filter(RegionFilter; Region) { }
                    dataitem(Line; "Qjsm Line")
                    {
                        DataItemLink = "Header No." = Header."No.";
                        SqlJoinType = InnerJoin;
                        column(LineTotal; Amount) { Method = Sum; }
                        filter(KindFilter; Kind) { }
                    }
                }
            }
        }

        // A FlowField column (synthesized FlowField-calculation dataitem in the raw list) beside
        // an aggregated column, with a filter-only element after it in dataitem order.
        query 62533 "QJSM Join FF Filter Only"
        {
            QueryType = Normal;
            OrderBy = ascending(HeaderNo);
            elements
            {
                dataitem(Line; "Qjsm Line")
                {
                    column(LineSum; Amount) { Method = Sum; }
                    filter(KindFilter; Kind) { }
                    dataitem(Header; "Qjsm Header")
                    {
                        DataItemLink = "No." = Line."Header No.";
                        SqlJoinType = InnerJoin;
                        column(HeaderNo; "No.") { }
                        column(HeaderTotal; "Total Amount") { }
                    }
                }
            }
        }

        codeunit 62534 "QJSM 3523 Tests"
        {
            Subtype = Test;

            local procedure Seed()
            var
                Header: Record "Qjsm Header";
                Line: Record "Qjsm Line";
            begin
                Header.DeleteAll();
                Line.DeleteAll();
                Header.Init(); Header."No." := 'H1'; Header.Region := 1; Header.Insert();
                Header.Init(); Header."No." := 'H2'; Header.Region := 2; Header.Insert();
                Header.Init(); Header."No." := 'H3'; Header.Region := 1; Header.Insert();
                Line.Init(); Line."Entry No." := 1; Line."Header No." := 'H1'; Line.Amount := 10; Line.Kind := 1; Line.Insert();
                Line.Init(); Line."Entry No." := 2; Line."Header No." := 'H1'; Line.Amount := 20; Line.Kind := 2; Line.Insert();
                Line.Init(); Line."Entry No." := 3; Line."Header No." := 'H2'; Line.Amount := 300; Line.Kind := 1; Line.Insert();
                Line.Init(); Line."Entry No." := 4; Line."Header No." := 'H3'; Line.Amount := 5; Line.Kind := 1; Line.Insert();
                Line.Init(); Line."Entry No." := 5; Line."Header No." := 'H3'; Line.Amount := 7; Line.Kind := 2; Line.Insert();
            end;

            // Unfiltered control: three groups with their own totals, so the filtered cases
            // below cannot pass by the whole query being broken.
            [Test]
            procedure Unfiltered_ReturnsEveryGroupWithItsOwnTotal()
            var
                Q: Query "QJSM Join Filter Only";
                Seen: Text;
            begin
                Seed();
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1=%2;', Q.HeaderNo, Q.LineTotal);
                Q.Close();
                if Seen <> 'H1=30;H2=300;H3=12;' then
                    Error('unfiltered groups wrong: %1', Seen);
            end;

            // Runtime filter on the PARENT's filter-only element: Region 1 keeps H1 and H3 and
            // drops H2 (whose group would otherwise sum to 300).
            [Test]
            procedure FilterOnlyOnParent_ExcludesOtherRegions()
            var
                Q: Query "QJSM Join Filter Only";
                Seen: Text;
            begin
                Seed();
                Q.SetRange(RegionFilter, 1);
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1=%2;', Q.HeaderNo, Q.LineTotal);
                Q.Close();
                if Seen <> 'H1=30;H3=12;' then
                    Error('Region 1 must keep H1 and H3 only: %1', Seen);
            end;

            // Runtime filter on the CHILD's filter-only element -- the second dataitem's extra
            // slot comes after the first dataitem's, so a layout that put them in the other
            // order would compare Region against Kind. Kind 1 drops the Kind 2 lines BEFORE
            // grouping: H1 sums to 10 (not 30) and H3 to 5 (not 12).
            [Test]
            procedure FilterOnlyOnChild_DropsRowsBeforeTheyAreSummed()
            var
                Q: Query "QJSM Join Filter Only";
                Seen: Text;
            begin
                Seed();
                Q.SetRange(KindFilter, 1);
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1=%2;', Q.HeaderNo, Q.LineTotal);
                Q.Close();
                if Seen <> 'H1=10;H2=300;H3=5;' then
                    Error('Kind 1 must sum only Kind 1 lines: %1', Seen);
            end;

            // Both filter-only elements at once, plus a HAVING on the aggregated column: the
            // HAVING reads the aggregated slot while the two filters read their own.
            [Test]
            procedure BothFilterOnly_AndHavingOnTheAggregate_AllApply()
            var
                Q: Query "QJSM Join Filter Only";
                Seen: Text;
            begin
                Seed();
                Q.SetRange(RegionFilter, 1);
                Q.SetRange(KindFilter, 1);
                Q.SetFilter(LineTotal, '>%1', 6);
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1=%2;', Q.HeaderNo, Q.LineTotal);
                Q.Close();
                // Region 1 + Kind 1: H1=10, H3=5. HAVING > 6 drops H3.
                if Seen <> 'H1=10;' then
                    Error('filters + HAVING must leave H1=10 only: %1', Seen);
            end;

            // HAVING alone, excluding a group on its aggregated value.
            [Test]
            procedure HavingOnTheAggregate_ExcludesSmallGroups()
            var
                Q: Query "QJSM Join Filter Only";
                Seen: Text;
            begin
                Seed();
                Q.SetFilter(LineTotal, '>%1', 20);
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1=%2;', Q.HeaderNo, Q.LineTotal);
                Q.Close();
                if Seen <> 'H1=30;H2=300;' then
                    Error('HAVING > 20 must drop H3 (12): %1', Seen);
            end;

            // The runtime filter is replaced by the next call and by Reset: the layout is
            // read per execution, so a later open must not see the earlier filter.
            [Test]
            procedure FilterIsReplacedAndReset_OnTheNextOpen()
            var
                Q: Query "QJSM Join Filter Only";
                Seen: Text;
            begin
                Seed();
                Q.SetRange(RegionFilter, 2);
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1=%2;', Q.HeaderNo, Q.LineTotal);
                Q.Close();
                if Seen <> 'H2=300;' then
                    Error('Region 2 must keep H2 only: %1', Seen);

                Clear(Seen);
                Q.SetRange(RegionFilter, 1);
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1=%2;', Q.HeaderNo, Q.LineTotal);
                Q.Close();
                if Seen <> 'H1=30;H3=12;' then
                    Error('replacing the filter with Region 1 must give H1 and H3: %1', Seen);

                Clear(Seen);
                Q.SetRange(RegionFilter);
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1=%2;', Q.HeaderNo, Q.LineTotal);
                Q.Close();
                if Seen <> 'H1=30;H2=300;H3=12;' then
                    Error('clearing the filter must restore every group: %1', Seen);
            end;

            // FlowField column + aggregate + a filter-only element on the driving dataitem:
            // the synthesized FlowField dataitem sits in the dataitem list, so the layout the
            // filter reads must still put KindFilter on its own slot. Kind 2 keeps only the
            // Kind 2 lines (H1: 20, H3: 7); the FlowField column keeps its own per-header total
            // (H1: 30, H3: 12), which no Kind filter changes.
            [Test]
            procedure FlowFieldColumn_WithFilterOnlyElement_FilterExcludesRows()
            var
                Q: Query "QJSM Join FF Filter Only";
                Seen: Text;
            begin
                Seed();
                Q.SetRange(KindFilter, 2);
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1:%2/%3;', Q.HeaderNo, Q.LineSum, Q.HeaderTotal);
                Q.Close();
                if Seen <> 'H1:20/30;H3:7/12;' then
                    Error('Kind 2 + FlowField must read H1:20/30, H3:7/12: %1', Seen);
            end;

            // A runtime filter on a NORMAL (projected, non-aggregated) column of the join: H2 only.
            [Test]
            procedure NormalColumnFilter_ExcludesOtherHeaders()
            var
                Q: Query "QJSM Join Filter Only";
                Seen: Text;
            begin
                Seed();
                Q.SetRange(HeaderNo, 'H2');
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1=%2;', Q.HeaderNo, Q.LineTotal);
                Q.Close();
                if Seen <> 'H2=300;' then
                    Error('HeaderNo H2 must keep H2 only: %1', Seen);
            end;

            // A runtime filter ON the FlowField column itself: its slot comes from the
            // FlowField-calculation synthesized dataitem. H3's total (12) fails "> 20".
            [Test]
            procedure FlowFieldColumnFilter_ExcludesRowsByTheCalculatedValue()
            var
                Q: Query "QJSM Join FF Filter Only";
                Seen: Text;
            begin
                Seed();
                Q.SetFilter(HeaderTotal, '>%1', 20);
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1:%2/%3;', Q.HeaderNo, Q.LineSum, Q.HeaderTotal);
                Q.Close();
                if Seen <> 'H1:30/30;H2:300/300;' then
                    Error('HeaderTotal > 20 must keep H1 and H2: %1', Seen);
            end;

            [Test]
            procedure FlowFieldColumn_WithHaving_ExcludesRows()
            var
                Q: Query "QJSM Join FF Filter Only";
                Seen: Text;
            begin
                Seed();
                Q.SetFilter(LineSum, '>%1', 100);
                Q.Open();
                while Q.Read() do
                    Seen += StrSubstNo('%1:%2/%3;', Q.HeaderNo, Q.LineSum, Q.HeaderTotal);
                Q.Close();
                if Seen <> 'H2:300/300;' then
                    Error('HAVING LineSum > 100 must keep H2 only: %1', Seen);
            end;
        }
        """);
        return root;
    }

    [SkippableFact]
    public async Task PostJoinFilters_ReadTheLayoutTheRowsWereProjectedWith()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var r = await SuiteServer.RunViaServer(bundle);

        r.AssertOutputDoesNotContain("EMIT-EXCLUDED");
        Assert.True(r.CompilationErrors.Count == 0, string.Join("\n", r.CompilationErrors));
        r.AssertCounts(passed: 10, failed: 0, errors: 0);
    }
}
