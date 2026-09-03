using System.Diagnostics;
using System.Text;
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
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

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

    private static string WriteBundle(string appJsonName, string idRangeFrom, string idRangeTo, string extraFiles)
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-query-join-flowfield-2423", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        {
          "id": "c7d1e4f2-2423-4a1b-9c3d-000000002423",
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
    // join -- unmeasured combination (no oracle case), so the runner must still throw loudly
    // rather than silently read a null/default GROUP BY key for the FlowField column.
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
        procedure JoinWithFlowFieldColumnAndGroupBy_ThrowsOutOfScope_InsteadOfSilentlyDefaulting()
        var
            Qjf3Header: Record "Qjf3 Header";
            Qjf3Line: Record "Qjf3 Line";
            Qjf3Link: Record "Qjf3 Link";
            Q: Query "QJF3 Join FlowField Group";
            Total: Decimal;
        begin
            Qjf3Header.Init(); Qjf3Header."No." := 'H1'; Qjf3Header.Insert();
            Qjf3Line.Init(); Qjf3Line."Entry No." := 1; Qjf3Line."Header No." := 'H1'; Qjf3Line.Amount := 7.25; Qjf3Line.Insert();
            Qjf3Link.Init(); Qjf3Link."Entry No." := 1; Qjf3Link."Header No." := 'H1'; Qjf3Link.Qty := 2; Qjf3Link.Insert();

            Q.Open();
            if not Q.Read() then
                Error('expected one row');
            Total := Q.TotalAmount;
            Q.Close();
            // If the runner ever silently returns a value here instead of throwing, this
            // assertion catches it directly.
            if Total <> 7.25 then
                Error('Expected the runner to throw RunnerOutOfScopeException (see #2423), not silently read %1', Total);
        end;
    }
    """;

    [SkippableFact]
    public void JoinWithFlowFieldColumn_OnChildDataItem_ReadsCalculatedValue()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle("QJF 2423 Repro", "62470", "62479", ChildSideShape);
        var (output, exitCode) = RunRunner(bundle);

        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        Assert.Contains("1P/0F/0E", output);
    }

    [SkippableFact]
    public void JoinWithFlowFieldColumn_OnParentDataItem_ReadsCalculatedValue()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle("QJF2 2423 Repro", "62480", "62489", ParentSideShape);
        var (output, exitCode) = RunRunner(bundle);

        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        Assert.Contains("1P/0F/0E", output);
    }

    [SkippableFact]
    public void JoinWithFlowFieldColumnAndGroupBy_ThrowsLoudOutOfScope_InsteadOfSilentlyDefaulting()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle("QJF3 2423 Repro", "62490", "62499", GroupByShape);
        var (output, exitCode) = RunRunner(bundle);

        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // The decisive assertions: the test FAILS at runtime because
        // RunnerOutOfScopeException propagates before the AL Error() call could ever fire,
        // and the failure carries the #2423 loud-failure guard's own reason string -- not a
        // silently-wrong AL-level "Expected the runner..." message, and not any unrelated
        // crash.
        Assert.Contains("query-join-flowfield-column-with-groupby-not-implemented", output);
        Assert.Contains("see docs/scope.md", output);
        Assert.DoesNotContain("Expected the runner to throw", output);
        Assert.Contains("0P/1F/0E", output);
    }
}
