using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2300 — a Query with a FlowField column NREs while BC builds the FlowField's
/// synthesized OuterApply sub-dataitem (NCLMetaQuery.CreateSubQueryForFlowFieldCalculation
/// → SqlTableDataProviderHelper.CreateDataItemFromFlowField → NCLMetaTable.SqlTableName →
/// NavSqlStatementHelper.ConvertToSqlIdentifier), because the skeleton NavSqlDatabaseProperties
/// leaves the private `invalidIdentifierChars` field null (GetUninitializedObject skips field
/// initializers) and ConvertToSqlIdentifier iterates it unconditionally.
///
/// Fixing that NRE alone was not the whole story: once the sub-dataitem's metadata built
/// successfully, TWO further mechanism bugs in THIS runner's own query-projection code
/// surfaced (neither is BC's own code):
///
///   1. RecordPatches.QueryProjection.DataAccessSource_GetDataAccessForQuery used
///      NCLMetaQueryDefinition.IncludedTables to decide whether a query is effectively
///      single-table. That BC-real property intentionally recurses into a FlowField
///      sub-dataitem's OWN inner table (BC's SQL needs it there) — so a query with one real
///      dataitem plus a FlowField column reported 2 included tables, always routing into the
///      (unrelated, and here unsupported) multi-dataitem JOIN path instead of the plain
///      single-table path.
///
///   2. BuildProjectionPlan iterated NCLMetaQueryDefinition.Columns, which flattens EVERY
///      dataitem's QueryColumns — including the FlowField sub-dataitem's own internal
///      aggregate column. That column's AggregationType (Sum/etc) routed it through the
///      #2137 GROUP BY machinery, and its SourceTableField resolves to the SOURCE table's
///      field (e.g. the summed field on the FlowField's source table) — NOT a field on the
///      OUTER row's own table. Reading that field's ColumnIndex against the outer row buffer
///      returned whatever unrelated field actually sits at that slot on the outer table
///      (observed: the outer table's own SystemId, a Guid — surfacing as
///      "NavNCLConversionException: Unable to convert from NavGuid to Int32" at the AL
///      assignment). NCLMetaQueryDataItem.SourceFlowField (set by BC's own
///      CreateSubQueryForFlowFieldCalculation) is the correct discriminator: a column whose
///      ParentDataItem carries one must be computed via FlowFieldPatches.
///      CalcOneFlowFieldForQueryRow (the same per-row FlowField calculation Record.CalcFields
///      uses), never via TableSlot or the generic aggregation path.
///
/// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it pins OUR OWN
/// pipeline (the skeleton-state fix plus the two projection-layer fixes above), not the BC
/// behavior itself. The BEHAVIORAL claim (a query FlowField column reads the same calculated
/// value Record.CalcFields would) is proven upstream against a live BC service tier — see
/// StefanMaron/BusinessCentral.AL.Language.Tests, per docs/rules/bc-behavior-tests-go-upstream.md.
///
/// Only the single-real-dataitem case is covered here. A query joining a Base Application
/// table (e.g. "Item Ledger Entry") and selecting one of ITS FlowFields hits a DIFFERENT,
/// unrelated NRE (NavQuery.ValidateExpectedType / ValidateTablesNotVirtual — the #2295 shape,
/// a namespace-qualified RelatedTable on a dependency table not being normalized) before ever
/// reaching the FlowField machinery this fix addresses — tracked separately.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryFlowFieldColumnProjectionTests
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

    private static string WriteBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-query-flowfield-2300", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c7d1e4f2-2300-4a1b-9c3d-000000002300",
          "name": "QFF 2300 Repro",
          "publisher": "Repro2300",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62460, "to": 62469 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "QffLine.al"), """
        table 62460 "QFF Line"
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
        """);
        File.WriteAllText(Path.Combine(root, "QffHeader.al"), """
        table 62461 "QFF Header"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; "Total Amount"; Decimal)
                {
                    FieldClass = FlowField;
                    CalcFormula = sum("QFF Line".Amount where("Header No." = field("No.")));
                }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }
        """);
        File.WriteAllText(Path.Combine(root, "QffQuery.al"), """
        query 62462 "QFF Header FlowField"
        {
            QueryType = Normal;
            elements
            {
                dataitem(QffHeader; "QFF Header")
                {
                    column(No; "No.") { }
                    column(TotalAmount; "Total Amount") { }
                }
            }
        }
        """);
        File.WriteAllText(Path.Combine(root, "QffTests.al"), """
        codeunit 62463 "QFF 2300 Tests"
        {
            Subtype = Test;

            [Test]
            procedure FlowFieldColumn_ReadsCalculatedValue()
            var
                QffHeader: Record "QFF Header";
                QffLine: Record "QFF Line";
                Q: Query "QFF Header FlowField";
                Total: Decimal;
            begin
                QffHeader.Init(); QffHeader."No." := 'H1'; QffHeader.Insert();
                QffLine.Init(); QffLine."Entry No." := 1; QffLine."Header No." := 'H1'; QffLine.Amount := 10.5; QffLine.Insert();
                QffLine.Init(); QffLine."Entry No." := 2; QffLine."Header No." := 'H1'; QffLine.Amount := 4.5; QffLine.Insert();

                Q.SetRange(No, 'H1');
                Q.Open();
                if not Q.Read() then
                    Error('expected one row');
                Total := Q.TotalAmount;
                Q.Close();
                if Total <> 15 then
                    Error('Expected 15, got %1', Total);
            end;

            [Test]
            procedure FlowFieldColumn_NoMatchingSourceRows_ReadsZero()
            var
                QffHeader: Record "QFF Header";
                Q: Query "QFF Header FlowField";
                Total: Decimal;
            begin
                QffHeader.Init(); QffHeader."No." := 'H2'; QffHeader.Insert();

                Q.SetRange(No, 'H2');
                Q.Open();
                if not Q.Read() then
                    Error('expected one row');
                Total := Q.TotalAmount;
                Q.Close();
                if Total <> 0 then
                    Error('Expected 0 (no matching QFF Line rows), got %1', Total);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void QueryFlowFieldColumn_ReadsCalculatedValue_InsteadOfCrashingOrCorruptingTheValue()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, exitCode) = RunRunner(bundle);

        // Never silently pass a run that failed to even get the test codeunit compiled/run.
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // The two crashes this issue reports, both must be gone.
        Assert.DoesNotContain("NavSqlStatementHelper.ConvertToSqlIdentifier", output);
        Assert.DoesNotContain("NavNCLConversionException", output);
        // Both tests must have run and passed — 2P/0F/0E is TestExecutor's own per-bundle
        // summary line (see CrossBundleModuleIdentityDedupTests for the same convention).
        Assert.Contains("2P/0F/0E", output);
    }
}
