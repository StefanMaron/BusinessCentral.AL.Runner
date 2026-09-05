using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2575 — a query column's ReverseSign property was ignored, so the value came back
/// un-negated. Confirmed absent on origin/main at 2eebaedd: no source file under AlRunner/
/// mentioned ReverseSign at all.
///
/// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it pins OUR OWN
/// pipeline —
///   1. BcAppSymbolCache.ParseQueryColumns reads the AL `ReverseSign = true` property out of
///      the compiled column's SymbolReference.json Properties bag; and
///   2. RecordPatches.NclMetaQueryBuilder.AddColumn carries it onto the design-time
///      MetaQueryColumn.ReverseSign, which NCLMetaQuery.CreateFromDesignMetadata reads to
///      populate the real NCLMetaQueryColumn.ReverseSign; and
///   3. RecordPatches.QueryProjection.cs's ApplyReverseSign (BuildRow / ComputeAggregateCore)
///      negates the value via BC's own FlowFieldsHelper.NegateValue — both for a plain column
///      and for a column that also declares Method = Sum.
///
/// Before the fix, step 1 dropped ReverseSign entirely (QueryColumnSymbol had no such field),
/// so every query column's NCLMetaQueryColumn.ReverseSign stayed at its CLR default (false) —
/// indistinguishable from a column that never declared the property — and the projection
/// layer never negated anything.
///
/// The BEHAVIORAL claim ("a column with ReverseSign = true returns the negated value, and a
/// sibling column over the same field without it does not") is proven upstream against a live
/// BC service tier — see StefanMaron/BusinessCentral.AL.Language.Tests PR #136, per
/// docs/rules/bc-behavior-tests-go-upstream.md. This test exists so a regression in OUR OWN
/// symbol-parsing → metadata-reconstruction → projection pipeline fails loudly here, without
/// waiting on that corpus PR to merge and the submodule pin to move.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryReverseSignProjectionTests
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
        var root = TestScratch.Dir("al-runner-query-reversesign-2575");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c7d1e4f2-2575-4a1b-9c3d-000000002575",
          "name": "QRS 2575 Repro",
          "publisher": "Repro2575",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 61980, "to": 61989 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "QrsOrder.al"), """
        table 61980 "QRS Order"
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

        query 61981 "QRS Order Reverse Sign"
        {
            QueryType = Normal;
            elements
            {
                dataitem(Order; "QRS Order")
                {
                    column(EntryNo; "Entry No.") { }
                    column(Amount; Amount) { }
                    column(ReversedAmount; Amount) { ReverseSign = true; }
                }
            }
        }

        query 61982 "QRS Order Sum Reverse Sign"
        {
            QueryType = Normal;
            elements
            {
                dataitem(Order; "QRS Order")
                {
                    column(CustNo; "Cust No.") { }
                    column(TotalAmount; Amount) { Method = Sum; ReverseSign = true; }
                }
            }
        }

        codeunit 61983 "QRS 2575 Tests"
        {
            Subtype = Test;

            // Plain (non-aggregated) column: ReverseSign = true must negate the value, and a
            // SIBLING column over the same field without the property must NOT be negated —
            // proving the runner reads the property per-column, not a blanket flip.
            [Test]
            procedure PlainColumn_ReverseSignNegates_SiblingWithoutItDoesNot()
            var
                Order: Record "QRS Order";
                Q: Query "QRS Order Reverse Sign";
                RowCount: Integer;
            begin
                Order.DeleteAll();
                Order.Init(); Order."Entry No." := 1; Order."Cust No." := 'C1'; Order.Amount := 100; Order.Insert();

                Q.Open();
                while Q.Read() do begin
                    RowCount += 1;
                    if Q.ReversedAmount <> -100 then
                        Error('ReverseSign = true must negate the value: expected -100, got %1', Q.ReversedAmount);
                    if Q.Amount <> 100 then
                        Error('A sibling column without ReverseSign must not be negated: expected 100, got %1', Q.Amount);
                end;
                Q.Close();

                if RowCount <> 1 then
                    Error('Expected exactly one row, got %1', RowCount);
            end;

            // Method = Sum combined with ReverseSign = true: the negation must apply to the
            // aggregated group total, both for same-sign rows (100+40=140 -> -140) and for a
            // mixed-sign group (100+-40=60 -> -60) — proving the negation is applied to the
            // result rather than, say, only when every contributing row shares one sign.
            [Test]
            procedure SumColumn_ReverseSignNegatesTheAggregatedTotal()
            var
                Order: Record "QRS Order";
                Q: Query "QRS Order Sum Reverse Sign";
                RowCount: Integer;
            begin
                Order.DeleteAll();
                Order.Init(); Order."Entry No." := 1; Order."Cust No." := 'C1'; Order.Amount := 100; Order.Insert();
                Order.Init(); Order."Entry No." := 2; Order."Cust No." := 'C1'; Order.Amount := -40; Order.Insert();

                Q.Open();
                while Q.Read() do begin
                    RowCount += 1;
                    if Q.TotalAmount <> -60 then
                        Error('ReverseSign on a Sum column must negate the group total: expected -60 (100+-40=60, negated), got %1', Q.TotalAmount);
                end;
                Q.Close();

                if RowCount <> 1 then
                    Error('Expected exactly one grouped row, got %1', RowCount);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void QueryColumnReverseSign_NegatesTheValue_InsteadOfSilentlyIgnoringTheProperty()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, exitCode) = RunRunner(bundle);

        // Never silently pass a run that failed to even get the test codeunit compiled/run.
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // Both tests must have run and passed — 2P/0F/0E is TestExecutor's own per-bundle
        // summary line (see QueryAggregationProjectionTests for the same convention).
        Assert.Contains("2P/0F/0E", output);
    }
}
