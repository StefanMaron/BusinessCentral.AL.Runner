using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2423 -- a multi-real-dataitem JOIN query that ALSO selects a FlowField column used
/// to silently read the column's typed default (observed: 0) instead of the calculated value,
/// once #2295 unblocked the query's own metadata construction. #2300 fixed the
/// single-real-dataitem FlowField case (via NCLMetaQueryDataItem.SourceFlowField and
/// FlowFieldPatches.CalcOneFlowFieldForQueryRow); #2423 extends the SAME mechanism across the
/// join projection path (AlRunner.QueryJoin.JoinExecutor.BuildJoinProjectionPlan and its
/// al-runner-side mirror RecordPatches.QueryProjection.ComputeJoinColumnSlotMap): the
/// FlowField sub-dataitem's column is now routed through ctx.CalcFlowFieldForRow against the
/// resolved OWNER real dataitem's row in the join combo, instead of a generic TableSlot read
/// against the FlowField's SOURCE table (a table the join never reads a row buffer for at all).
///
/// This is a RUNNER-MECHANISM suite: it pins that the runner (a) now COMPUTES the value
/// correctly for a FlowField column on either side of a join (this class's own #2423 fix,
/// verified independently upstream by StefanMaron/BusinessCentral.AL.Language.Tests#106's
/// "JoinFlowFieldColumn_ReadsCalculatedValue" against real BC), and (b) still fails LOUDLY
/// (never silently) for the one sub-shape left unimplemented: a FlowField column combined with
/// the join's own #2146 implicit GROUP BY (some other column aggregated Sum/Count/Average/
/// Min/Max) -- unmeasured, no oracle case covers it, and BuildGroupedRows' ResolveComboValue
/// has no FlowField branch.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryJoinFlowFieldColumnOosTests
{

    private static string WriteBundle(string appJsonName, string idRangeFrom, string idRangeTo, string extraFiles)
    {
        var root = TestScratch.Dir("al-runner-query-join-flowfield-2423");
        Directory.CreateDirectory(root);
        // A fresh app id per bundle: the facts share one server, which refuses two different apps
        // declaring one id (docs/shared-cli-server.md#suite-server).

        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "{{appJsonName}}",
          "publisher": "Repro2423",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": {{idRangeFrom}}, "to": {{idRangeTo}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "extra.al"), extraFiles);
        return root;
    }

    // Entirely application-local (no dependency needed): "Qjf Link" joins to "Qjf Header",
    // whose "Total Amount" is a FlowField summing "Qjf Line" -- the same shape as #2300's
    // JoinIleFlowFieldColumn_ReadsCalculatedValue (a local table inner-joined to a table
    // whose FlowField column is selected), just without the Base Application dependency a C#
    // fixture app.json may not declare (no-base-app-in-csharp-tests.md).
    private const string ChildSideShape = """
    table 62470 "Qjf Line"
    {
        DataClassification = SystemMetadata;
        fields
        {
            field(1; "Entry No."; Integer) { }
            field(2; "Header No."; Code[20]) { }
            field(3; Amount; Decimal) { }
        }
        keys { key(PK; "Entry No.") { Clustered = true; } }
    }
    table 62471 "Qjf Header"
    {
        DataClassification = SystemMetadata;
        fields
        {
            field(1; "No."; Code[20]) { }
            field(2; "Total Amount"; Decimal)
            {
                FieldClass = FlowField;
                CalcFormula = sum("Qjf Line".Amount where("Header No." = field("No.")));
            }
        }
        keys { key(PK; "No.") { Clustered = true; } }
    }
    table 62472 "Qjf Link"
    {
        DataClassification = SystemMetadata;
        fields
        {
            field(1; "Entry No."; Integer) { }
            field(2; "Header No."; Code[20]) { }
        }
        keys { key(PK; "Entry No.") { Clustered = true; } }
    }
    // FlowField column ("Total Amount") is on the CHILD (non-driving, joined) dataitem.
    query 62473 "QJF Join FlowField"
    {
        QueryType = Normal;
        elements
        {
            dataitem(QjfLink; "Qjf Link")
            {
                column(LinkEntryNo; "Entry No.") { }
                dataitem(QjfHeader; "Qjf Header")
                {
                    DataItemLink = "No." = QjfLink."Header No.";
                    SqlJoinType = InnerJoin;
                    column(TotalAmount; "Total Amount") { }
                }
            }
        }
    }
    codeunit 62474 "QJF 2423 Tests"
    {
        Subtype = Test;

        [Test]
        procedure JoinWithFlowFieldColumn_ReadsCalculatedValue()
        var
            QjfHeader: Record "Qjf Header";
            QjfLine: Record "Qjf Line";
            QjfLink: Record "Qjf Link";
            Q: Query "QJF Join FlowField";
            Total: Decimal;
        begin
            QjfHeader.Init(); QjfHeader."No." := 'H1'; QjfHeader.Insert();
            QjfLine.Init(); QjfLine."Entry No." := 1; QjfLine."Header No." := 'H1'; QjfLine.Amount := 7.25; QjfLine.Insert();
            QjfLink.Init(); QjfLink."Entry No." := 1; QjfLink."Header No." := 'H1'; QjfLink.Insert();

            Q.Open();
            if not Q.Read() then
                Error('expected one row');
            Total := Q.TotalAmount;
            Q.Close();
            if Total <> 7.25 then
                Error('Expected 7.25, got %1', Total);
        end;
    }
    """;

    // Same fixture shape, but the FlowField column ("Total Amount") is on the DRIVING (first,
    // parent) dataitem instead of the child -- the join's own combo-lookup and owner
    // resolution must not depend on which side of the join carries the FlowField (#2423
    // acceptance criteria: "either side").
    private const string ParentSideShape = """
    table 62480 "Qjf2 Line"
    {
        DataClassification = SystemMetadata;
        fields
        {
            field(1; "Entry No."; Integer) { }
            field(2; "Header No."; Code[20]) { }
            field(3; Amount; Decimal) { }
        }
        keys { key(PK; "Entry No.") { Clustered = true; } }
    }
    table 62481 "Qjf2 Header"
    {
        DataClassification = SystemMetadata;
        fields
        {
            field(1; "No."; Code[20]) { }
            field(2; "Total Amount"; Decimal)
            {
                FieldClass = FlowField;
                CalcFormula = sum("Qjf2 Line".Amount where("Header No." = field("No.")));
            }
        }
        keys { key(PK; "No.") { Clustered = true; } }
    }
    table 62482 "Qjf2 Link"
    {
        DataClassification = SystemMetadata;
        fields
        {
            field(1; "Entry No."; Integer) { }
            field(2; "Header No."; Code[20]) { }
        }
        keys { key(PK; "Entry No.") { Clustered = true; } }
    }
    // FlowField column ("Total Amount") is on the PARENT (driving) dataitem.
    query 62483 "QJF2 Join FlowField"
    {
        QueryType = Normal;
        elements
        {
            dataitem(Qjf2Header; "Qjf2 Header")
            {
                column(TotalAmount; "Total Amount") { }
                dataitem(Qjf2Link; "Qjf2 Link")
                {
                    DataItemLink = "Header No." = Qjf2Header."No.";
                    SqlJoinType = InnerJoin;
                    column(LinkEntryNo; "Entry No.") { }
                }
            }
        }
    }
    codeunit 62484 "QJF2 2423 Tests"
    {
        Subtype = Test;

        [Test]
        procedure JoinWithFlowFieldOnParentDataItem_ReadsCalculatedValue()
        var
            Qjf2Header: Record "Qjf2 Header";
            Qjf2Line: Record "Qjf2 Line";
            Qjf2Link: Record "Qjf2 Link";
            Q: Query "QJF2 Join FlowField";
            Total: Decimal;
        begin
            Qjf2Header.Init(); Qjf2Header."No." := 'H1'; Qjf2Header.Insert();
            Qjf2Line.Init(); Qjf2Line."Entry No." := 1; Qjf2Line."Header No." := 'H1'; Qjf2Line.Amount := 3.5; Qjf2Line.Insert();
            Qjf2Line.Init(); Qjf2Line."Entry No." := 2; Qjf2Line."Header No." := 'H1'; Qjf2Line.Amount := 6.5; Qjf2Line.Insert();
            Qjf2Link.Init(); Qjf2Link."Entry No." := 1; Qjf2Link."Header No." := 'H1'; Qjf2Link.Insert();

            Q.Open();
            if not Q.Read() then
                Error('expected one row');
            Total := Q.TotalAmount;
            Q.Close();
            if Total <> 10 then
                Error('Expected 10, got %1', Total);
        end;
    }
    """;

    // FlowField column + a #2146 implicit GROUP BY (another column Method = Sum) in the SAME
    // join -- #2455 gave ResolveComboValue a FlowField branch, so this now groups correctly
    // instead of throwing (verified against real BC 28.4, see #2455).
    private const string GroupByShape = """
    table 62490 "Qjf3 Line"
    {
        DataClassification = SystemMetadata;
        fields
        {
            field(1; "Entry No."; Integer) { }
            field(2; "Header No."; Code[20]) { }
            field(3; Amount; Decimal) { }
        }
        keys { key(PK; "Entry No.") { Clustered = true; } }
    }
    table 62491 "Qjf3 Header"
    {
        DataClassification = SystemMetadata;
        fields
        {
            field(1; "No."; Code[20]) { }
            field(2; "Total Amount"; Decimal)
            {
                FieldClass = FlowField;
                CalcFormula = sum("Qjf3 Line".Amount where("Header No." = field("No.")));
            }
        }
        keys { key(PK; "No.") { Clustered = true; } }
    }
    table 62492 "Qjf3 Link"
    {
        DataClassification = SystemMetadata;
        fields
        {
            field(1; "Entry No."; Integer) { }
            field(2; "Header No."; Code[20]) { }
            field(3; Qty; Integer) { }
        }
        keys { key(PK; "Entry No.") { Clustered = true; } }
    }
    query 62493 "QJF3 Join FlowField Group"
    {
        QueryType = Normal;
        elements
        {
            dataitem(Qjf3Link; "Qjf3 Link")
            {
                column(SumQty; Qty) { Method = Sum; }
                dataitem(Qjf3Header; "Qjf3 Header")
                {
                    DataItemLink = "No." = Qjf3Link."Header No.";
                    SqlJoinType = InnerJoin;
                    column(TotalAmount; "Total Amount") { }
                }
            }
        }
    }
    codeunit 62494 "QJF3 2423 Tests"
    {
        Subtype = Test;

        [Test]
        procedure JoinWithFlowFieldColumnAndGroupBy_ReadsGroupedFlowFieldValue()
        var
            Qjf3Header: Record "Qjf3 Header";
            Qjf3Line: Record "Qjf3 Line";
            Qjf3Link: Record "Qjf3 Link";
            Q: Query "QJF3 Join FlowField Group";
            Total: Decimal;
            SumQty: Integer;
        begin
            Qjf3Header.Init(); Qjf3Header."No." := 'H1'; Qjf3Header.Insert();
            Qjf3Line.Init(); Qjf3Line."Entry No." := 1; Qjf3Line."Header No." := 'H1'; Qjf3Line.Amount := 7.25; Qjf3Line.Insert();
            Qjf3Link.Init(); Qjf3Link."Entry No." := 1; Qjf3Link."Header No." := 'H1'; Qjf3Link.Qty := 2; Qjf3Link.Insert();

            Q.Open();
            if not Q.Read() then
                Error('expected one row');
            Total := Q.TotalAmount;
            SumQty := Q.SumQty;
            if Q.Read() then
                Error('expected exactly one group');
            Q.Close();
            if (Total <> 7.25) or (SumQty <> 2) then
                Error('Expected group TotalAmount=7.25 SumQty=2, got %1/%2', Total, SumQty);
        end;
    }
    """;

    [SkippableFact]
    public async Task JoinWithFlowFieldColumn_OnChildDataItem_ReadsCalculatedValue()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle("QJF 2423 Repro", "62470", "62479", ChildSideShape);
        var result = await SuiteServer.RunViaServer(bundle);

        result.AssertOutputDoesNotContain("EMIT-EXCLUDED");
        Assert.Empty(result.CompilationErrors);
        result.AssertCounts(passed: 1, failed: 0, errors: 0);
    }

    [SkippableFact]
    public async Task JoinWithFlowFieldColumn_OnParentDataItem_ReadsCalculatedValue()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle("QJF2 2423 Repro", "62480", "62489", ParentSideShape);
        var result = await SuiteServer.RunViaServer(bundle);

        result.AssertOutputDoesNotContain("EMIT-EXCLUDED");
        Assert.Empty(result.CompilationErrors);
        result.AssertCounts(passed: 1, failed: 0, errors: 0);
    }

    [SkippableFact]
    public async Task JoinWithFlowFieldColumnAndGroupBy_ReadsGroupedFlowFieldValue()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle("QJF3 2455 Repro", "62490", "62499", GroupByShape);
        var result = await SuiteServer.RunViaServer(bundle);

        result.AssertOutputDoesNotContain("EMIT-EXCLUDED");
        Assert.Empty(result.CompilationErrors);
        result.AssertOutputDoesNotContain("query-join-flowfield-column-with-groupby-not-implemented");
        result.AssertCounts(passed: 1, failed: 0, errors: 0);
    }
}
