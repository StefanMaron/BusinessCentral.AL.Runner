using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2146 — follow-up to #2137's single-dataitem GROUP BY aggregation
/// (RecordPatches.QueryProjection.cs). Two related surfaces used to throw
/// RunnerOutOfScopeException instead of computing a real (or a silently wrong) answer:
///
///   1. A runtime SetRange/SetFilter on an AGGREGATED column (Method = Sum/Count/Average/
///      Min/Max) is a HAVING-clause filter — evaluated against the per-GROUP aggregated
///      result, not the raw pre-aggregation row. TranslateQueryFilters now excludes such
///      filters from the WHERE-style push-down and ApplyHavingFilters evaluates them AFTER
///      ProjectQueryRows has grouped/aggregated.
///   2. A multi-dataitem JOIN query with any aggregated column needs its own GROUP BY over
///      the JOINED rows — AlRunner.QueryJoin.JoinExecutor.BuildGroupedRows now performs it,
///      sharing the actual Sum/Count/Average/Min/Max math with the single-dataitem path via
///      JoinContext.ComputeAggregate → RecordPatches.QueryProjection.Join_ComputeAggregate →
///      ComputeAggregateCore.
///
/// This is a RUNNER-MECHANISM test, not a claim about what real BC does — same posture as
/// QueryAggregationProjectionTests.cs for #2137. The BEHAVIORAL claim (what BC itself does
/// with a HAVING-style runtime filter, and with a JOIN+GROUP BY) HAS been adjudicated by the
/// al-language corpus's own CI against a real BC service tier: it merged as
/// StefanMaron/BusinessCentral.AL.Language.Tests#74 (commit 6262dd6506dd20a39ee1626ed6a0ddd24d0685cd
/// on master), and all three corpus tests
/// (FilterOnAggregatedColumn_EvaluatesAgainstGroupResult_NotRawRow,
/// FilterOnAggregatedColumn_ExcludingEveryGroup_ReturnsNoRows,
/// JoinWithAggregatedColumn_GroupsJoinedRows_NotOneRowPerPair) pass on both BC 27.5 and
/// BC 28.3 — real BC agrees with this implementation. The submodule pin in this repo is bumped
/// to that commit alongside this fix. This test remains as the RUNNER-MECHANISM regression
/// guard: it exists so a regression in OUR OWN filter-extraction/grouping/aggregation
/// pipeline (BcAppSymbolCache → RecordPatches.NclMetaQueryBuilder →
/// RecordPatches.QueryProjection/AlRunner.QueryJoin.JoinExecutor) fails loudly here, without
/// needing a full corpus run to notice.
///
/// Both scenarios below are deliberately designed so a NAIVE (wrong) implementation would
/// produce a DIFFERENT, distinguishable answer from the one asserted:
///   - The HAVING scenario picks raw per-row values that never individually satisfy the
///     filter, only their per-group SUM does — a WHERE-style (pre-aggregation, per-row)
///     application of the same filter expression would silently produce a DIFFERENT result
///     (either the wrong group survives, or none does), not merely "still pass".
///   - The JOIN+GROUP BY scenario asserts an exact row COUNT smaller than the number of raw
///     joined rows — an ungrouped (bug-#2137-class) implementation would echo one row PER
///     JOINED PAIR instead of one row per group, so the row-count assertion alone would fail.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryHavingAndJoinAggregationProjectionTests
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
        var root = Path.Combine(Path.GetTempPath(), "al-runner-query-having-join-2146", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "d8e2f5a3-2146-4b2c-9d4e-000000002146",
          "name": "QHJ 2146 Repro",
          "publisher": "Repro2146",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 61900, "to": 61930 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "QhjHaving.al"), """
        table 61900 "QHJ Order"
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

        query 61901 "QHJ Order Sum"
        {
            QueryType = Normal;
            OrderBy = ascending(CustNo);
            elements
            {
                dataitem(Order; "QHJ Order")
                {
                    column(CustNo; "Cust No.") { }
                    column(TotalAmount; Amount) { Method = Sum; }
                    column(CountAmount) { Method = Count; }
                }
            }
        }

        table 61902 "QHJ Customer"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; "Name"; Text[50]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        table 61903 "QHJ Cust Order"
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

        query 61904 "QHJ Cust Order Sum"
        {
            QueryType = Normal;
            OrderBy = ascending(CustNo);
            elements
            {
                dataitem(Customer; "QHJ Customer")
                {
                    column(CustNo; "No.") { }

                    dataitem(Ord; "QHJ Cust Order")
                    {
                        DataItemLink = "Cust No." = Customer."No.";
                        SqlJoinType = InnerJoin;

                        column(TotalAmount; Amount) { Method = Sum; }
                        column(CountOrders) { Method = Count; }
                    }
                }
            }
        }

        codeunit 61905 "QHJ 2146 Tests"
        {
            Subtype = Test;

            local procedure InitHaving()
            var
                Order: Record "QHJ Order";
            begin
                Order.DeleteAll();
                // C1's two raw rows (60, 60) never individually satisfy "> 100" — only their
                // SUM (120) does. C2's single raw row (100) also never satisfies "> 100"
                // individually, and its SUM (100) doesn't either. A WHERE-style (pre-
                // aggregation, per-row) application of this same filter would keep NEITHER
                // customer (every raw row is <= 100), producing ZERO groups — a different,
                // distinguishable wrong answer from the correct HAVING result below.
                Order.Init(); Order."Entry No." := 1; Order."Cust No." := 'C1'; Order.Amount := 60; Order.Insert();
                Order.Init(); Order."Entry No." := 2; Order."Cust No." := 'C1'; Order.Amount := 60; Order.Insert();
                Order.Init(); Order."Entry No." := 3; Order."Cust No." := 'C2'; Order.Amount := 100; Order.Insert();
            end;

            [Test]
            procedure HavingFilterOnAggregatedColumn_EvaluatesAgainstGroupResult_NotRawRow()
            var
                Q: Query "QHJ Order Sum";
                RowCount: Integer;
            begin
                InitHaving();
                Q.SetFilter(TotalAmount, '>%1', 100);
                Q.Open();
                while Q.Read() do begin
                    RowCount += 1;
                    if Q.CustNo <> 'C1' then
                        Error('HAVING TotalAmount>100 must keep only C1 (sum 120), got CustNo %1', Q.CustNo);
                    if Q.TotalAmount <> 120 then
                        Error('C1''s aggregated TotalAmount must be 120 (60+60), got %1', Q.TotalAmount);
                    if Q.CountAmount <> 2 then
                        Error('C1''s aggregated CountAmount must be 2, got %1', Q.CountAmount);
                end;
                Q.Close();

                if RowCount <> 1 then
                    Error('HAVING TotalAmount>100 must keep exactly 1 group (C1), got %1 rows', RowCount);
            end;

            // Negative sibling: a value NO group's aggregate satisfies must close the resultset,
            // proving the filter is genuinely evaluated (not a no-op that lets everything through).
            [Test]
            procedure HavingFilterOnAggregatedColumn_ExcludingEveryGroup_ReturnsNoRows()
            var
                Q: Query "QHJ Order Sum";
            begin
                InitHaving();
                Q.SetFilter(TotalAmount, '>%1', 1000); // neither group's sum (120, 100) qualifies
                Q.Open();
                if Q.Read() then
                    Error('HAVING TotalAmount>1000 must exclude every group (max sum is 120)');
                Q.Close();
            end;

            local procedure InitJoin()
            var
                Cust: Record "QHJ Customer";
                Ord: Record "QHJ Cust Order";
            begin
                Cust.DeleteAll();
                Ord.DeleteAll();
                Cust.Init(); Cust."No." := 'C1'; Cust.Name := 'Alice'; Cust.Insert();
                Cust.Init(); Cust."No." := 'C2'; Cust.Name := 'Bob'; Cust.Insert();
                Ord.Init(); Ord."Entry No." := 1; Ord."Cust No." := 'C1'; Ord.Amount := 100; Ord.Insert();
                Ord.Init(); Ord."Entry No." := 2; Ord."Cust No." := 'C1'; Ord.Amount := 200; Ord.Insert();
                Ord.Init(); Ord."Entry No." := 3; Ord."Cust No." := 'C2'; Ord.Amount := 50; Ord.Insert();
            end;

            // C1 has TWO order rows and C2 has ONE — an ungrouped (bug-#2137-class) join would
            // echo one row PER JOINED PAIR (3 rows total, each with its own unsummed Amount).
            // The correct GROUP BY answer is exactly 2 rows (one per customer), each carrying
            // the SUM/COUNT over that customer's own orders only.
            [Test]
            procedure JoinWithAggregatedColumn_GroupsJoinedRows_InsteadOfEchoingRawPairs()
            var
                Q: Query "QHJ Cust Order Sum";
                RowCount: Integer;
                C1Seen, C2Seen : Boolean;
            begin
                InitJoin();
                Q.Open();
                while Q.Read() do begin
                    RowCount += 1;
                    case Q.CustNo of
                        'C1':
                            begin
                                C1Seen := true;
                                if Q.TotalAmount <> 300 then
                                    Error('C1''s joined+grouped TotalAmount must be 300 (100+200), got %1', Q.TotalAmount);
                                if Q.CountOrders <> 2 then
                                    Error('C1''s joined+grouped CountOrders must be 2, got %1', Q.CountOrders);
                            end;
                        'C2':
                            begin
                                C2Seen := true;
                                if Q.TotalAmount <> 50 then
                                    Error('C2''s joined+grouped TotalAmount must be 50, got %1', Q.TotalAmount);
                                if Q.CountOrders <> 1 then
                                    Error('C2''s joined+grouped CountOrders must be 1, got %1', Q.CountOrders);
                            end;
                        else
                            Error('Unexpected CustNo %1 - grouping over the join produced an extra/wrong group', Q.CustNo);
                    end;
                end;
                Q.Close();

                if not (C1Seen and C2Seen) then
                    Error('Expected exactly one grouped row per customer (C1Seen=%1, C2Seen=%2)', C1Seen, C2Seen);
                if RowCount <> 2 then
                    Error('JOIN+GROUP BY must return exactly 2 rows (one per customer), not one per joined pair; got %1', RowCount);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void HavingFilterAndJoinAggregation_ProduceRealAnswers_InsteadOfThrowingOos()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, exitCode) = RunRunner(bundle);

        // Never silently pass a run that failed to even get the test codeunit compiled/run.
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // Neither surface may still throw the OOS reasons #2146 tracks turning into real
        // implementations — a regression back to "throw" would otherwise still show 3P/0F/0E
        // as long as every test asserterror'd correctly, so check the reason strings directly.
        Assert.DoesNotContain("query-having-filter-not-supported", output);
        Assert.DoesNotContain("query-join-aggregation-not-supported", output);
        // All three tests must have run and passed — 3P/0F/0E is TestExecutor's own per-bundle
        // summary line (see CrossBundleModuleIdentityDedupTests for the same convention).
        Assert.Contains("3P/0F/0E", output);
    }
}
