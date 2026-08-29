using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2137 — a query column with Method = Sum/Count/Average/Min/Max silently returned
/// unaggregated, ungrouped rows (each raw row echoing its own unsummed source value) instead
/// of the implicit GROUP BY real BC's compiled SQL performs.
///
/// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it pins OUR OWN
/// pipeline —
///   1. BcAppSymbolCache.ParseQueryColumns reads the AL `Method = ...` property out of the
///      compiled column's SymbolReference.json Properties bag; and
///   2. RecordPatches.NclMetaQueryBuilder.AddColumn carries it onto the design-time
///      MetaQueryColumn.FieldTotalingMethod, which NCLMetaQuery.CreateDynamicQuery reads to
///      populate the real NCLMetaQueryColumn.AggregationType; and
///   3. RecordPatches.QueryProjection.cs's ProjectQueryRows groups by the non-aggregated
///      columns and computes each aggregate over its group (BuildRow/ComputeAggregate).
///
/// Before the fix, step 1 dropped Method entirely (QueryColumnSymbol had no such field), so
/// every query column's AggregationType stayed at its default (None) — indistinguishable
/// from an ordinary column — and the projection layer never grouped anything.
///
/// The BEHAVIORAL claim ("a Method = Sum column groups by the query's other columns and
/// aggregates real BC's own way, for a scalar aggregate too") is proven upstream against a
/// live BC service tier — see StefanMaron/BusinessCentral.AL.Language.Tests, per
/// docs/rules/bc-behavior-tests-go-upstream.md. This test exists so a regression in OUR OWN
/// symbol-parsing → metadata-reconstruction → projection pipeline fails loudly here, without
/// waiting on that corpus PR to merge and the submodule pin to move.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryAggregationProjectionTests
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
        var root = Path.Combine(Path.GetTempPath(), "al-runner-query-agg-2137", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c7d1e4f2-2137-4a1b-9c3d-000000002137",
          "name": "QAP 2137 Repro",
          "publisher": "Repro2137",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61890, "to": 61899 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "QapOrder.al"), """
        table 61890 "QAP Order"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; "Cust No."; Code[20]) { }
                field(3; Amount; Decimal) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        query 61891 "QAP Order Sum"
        {
            QueryType = Normal;
            elements
            {
                dataitem(Order; "QAP Order")
                {
                    column(CustNo; "Cust No.") { }
                    column(TotalAmount; Amount) { Method = Sum; }
                    column(CountAmount) { Method = Count; }
                }
            }
        }

        query 61892 "QAP Order Scalar Sum"
        {
            QueryType = Normal;
            elements
            {
                dataitem(Order; "QAP Order")
                {
                    column(TotalAmount; Amount) { Method = Sum; }
                    column(CountAmount) { Method = Count; }
                }
            }
        }

        codeunit 61893 "QAP 2137 Tests"
        {
            Subtype = Test;

            local procedure Initialize()
            var
                Order: Record "QAP Order";
            begin
                Order.DeleteAll();
                Order.Init(); Order."Entry No." := 1; Order."Cust No." := 'C1'; Order.Amount := 100; Order.Insert();
                Order.Init(); Order."Entry No." := 2; Order."Cust No." := 'C1'; Order.Amount := 200; Order.Insert();
                Order.Init(); Order."Entry No." := 3; Order."Cust No." := 'C2'; Order.Amount := 50; Order.Insert();
            end;

            // The exact issue #2137 reproducer, generalised to two groups: real BC groups by
            // the non-aggregated CustNo column and sums/counts Amount PER GROUP. Before the
            // fix, every raw row echoed its own unsummed Amount and a Count of 1 — this
            // asserts the grouped, aggregated answer instead.
            [Test]
            procedure GroupedSumAndCount_MatchPerGroupTotals()
            var
                Q: Query "QAP Order Sum";
                C1Seen, C2Seen : Boolean;
            begin
                Initialize();
                Q.Open();
                while Q.Read() do begin
                    case Q.CustNo of
                        'C1':
                            begin
                                C1Seen := true;
                                if Q.TotalAmount <> 300 then
                                    Error('C1 TotalAmount must be 300 (100+200), got %1', Q.TotalAmount);
                                if Q.CountAmount <> 2 then
                                    Error('C1 CountAmount must be 2, got %1', Q.CountAmount);
                            end;
                        'C2':
                            begin
                                C2Seen := true;
                                if Q.TotalAmount <> 50 then
                                    Error('C2 TotalAmount must be 50, got %1', Q.TotalAmount);
                                if Q.CountAmount <> 1 then
                                    Error('C2 CountAmount must be 1, got %1', Q.CountAmount);
                            end;
                        else
                            Error('Unexpected CustNo %1 - grouping produced an extra/wrong group', Q.CustNo);
                    end;
                end;
                Q.Close();

                if not (C1Seen and C2Seen) then
                    Error('Expected exactly one grouped row per CustNo (C1Seen=%1, C2Seen=%2)', C1Seen, C2Seen);
            end;

            // Negative/edge case: a query with ONLY aggregate columns (no grouping column at
            // all) is BC's scalar-aggregate case — exactly one output row over the WHOLE
            // table, even when it is empty. Before the fix this either produced one row per
            // raw source row (non-empty case) or zero rows at all (empty case) — neither of
            // which is BC's single always-one-row scalar-aggregate answer.
            [Test]
            procedure ScalarAggregate_OverEmptyTable_StillProducesOneDefaultedRow()
            var
                Order: Record "QAP Order";
                Q: Query "QAP Order Scalar Sum";
                RowCount: Integer;
            begin
                Order.DeleteAll();
                Q.Open();
                while Q.Read() do begin
                    RowCount += 1;
                    if Q.TotalAmount <> 0 then
                        Error('Sum over an empty table must default to 0, got %1', Q.TotalAmount);
                    if Q.CountAmount <> 0 then
                        Error('Count over an empty table must default to 0, got %1', Q.CountAmount);
                end;
                Q.Close();

                if RowCount <> 1 then
                    Error('A scalar aggregate (no grouping column) must always return exactly ONE row, got %1', RowCount);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void QueryColumnAggregation_GroupsAndAggregates_InsteadOfSilentlyEchoingRawRows()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, exitCode) = RunRunner(bundle);

        // Never silently pass a run that failed to even get the test codeunit compiled/run.
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // Both tests must have run and passed — 2P/0F/0E is TestExecutor's own per-bundle
        // summary line (see CrossBundleModuleIdentityDedupTests for the same convention).
        Assert.Contains("2P/0F/0E", output);
    }
}
