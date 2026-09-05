using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2418 — a query column's static <c>ColumnFilter</c> property was parsed nowhere:
/// <c>BcAppSymbolCache.ParseQueryColumns</c> carried only <c>Caption</c>/<c>Method</c> (#2137),
/// so <c>RecordPatches.NclMetaQueryBuilder</c> never saw the filter and the synthesized
/// <c>MetaQuery.ColumnFilters</c> design list stayed empty; even if it had been populated,
/// <c>RecordPatches.QueryProjection.TranslateQueryFilters</c> only read the request's RUNTIME
/// filters (<c>SetRange</c>/<c>SetFilter</c>), never <c>NCLMetaQuery.ColumnFilters</c> (the
/// static ones BC's own <c>CreateDynamicQuery</c>/<c>BuildFilterExpressionCollection</c> would
/// build). On a <c>Method = Sum</c> column this is a HAVING-clause filter (drops groups whose
/// aggregated total fails it); on a plain column it is a WHERE-clause filter (drops raw rows).
/// A runtime <c>SetRange</c>/<c>SetFilter</c> on the SAME column REPLACES the static filter
/// rather than combining with it.
///
/// This is a RUNNER-MECHANISM test, not a claim about what real BC does — same posture as
/// QueryHavingAndJoinAggregationProjectionTests.cs for #2146. The BEHAVIORAL claim (what BC
/// itself does with a static ColumnFilter on an aggregated vs. a plain column, and that a
/// runtime filter on the same column replaces it) is being adjudicated by the al-language
/// corpus's own CI against a real BC service tier
/// (StefanMaron/BusinessCentral.AL.Language.Tests, query/TestQueryColumnFilter.al) — the
/// submodule pin bump is a follow-up once that corpus PR merges, per
/// .claude/rules/al-language-submodule.md. This test exists so a regression in OUR OWN
/// ColumnFilter-parsing/design-population/WHERE-HAVING-routing pipeline
/// (BcAppSymbolCache → RecordPatches.AlSourceParser.TryParseColumnFilterText →
/// RecordPatches.NclMetaQueryBuilder.BuildMetaQueryDesign →
/// RecordPatches.QueryProjection.TranslateQueryFilters) fails loudly here without needing a
/// full corpus run to notice.
///
/// Both scenarios are deliberately designed so a naive (wrong/no-op) implementation would
/// produce a DIFFERENT, distinguishable answer:
///   - The Sum scenario has a group whose total is exactly 0 (100 + -100) — a no-op
///     ColumnFilter would let it through alongside the positive-total group, so the row COUNT
///     assertion alone fails if the filter is dropped.
///   - The plain-column scenario has rows for TWO customers — a no-op ColumnFilter would
///     return both, so asserting only the filtered customer's rows appear fails if dropped.
///   - The runtime-replaces-static scenario picks a runtime filter that the STATIC filter
///     alone would reject (C1's total is 0, failing "> 0") — if the runtime filter were
///     merely ANDed with the static one instead of replacing it, the result would stay empty.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryColumnFilterProjectionTests
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
        var root = TestScratch.Dir("al-runner-query-columnfilter-2418");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "d8e2f5a3-2418-4b2c-9d4e-000000002418",
          "name": "QCF 2418 Repro",
          "publisher": "Repro2418",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62480, "to": 62489 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "QcfColumnFilter.al"), """
        table 62480 "QCF Order"
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

        query 62481 "QCF Order Sum Filtered"
        {
            QueryType = Normal;
            OrderBy = ascending(CustNo);
            elements
            {
                dataitem(Order; "QCF Order")
                {
                    column(CustNo; "Cust No.") { }
                    column(TotalAmount; Amount)
                    {
                        ColumnFilter = TotalAmount = filter(> 0);
                        Method = Sum;
                    }
                }
            }
        }

        query 62482 "QCF Order Filtered"
        {
            QueryType = Normal;
            OrderBy = ascending(EntryNo);
            elements
            {
                dataitem(Order; "QCF Order")
                {
                    column(EntryNo; "Entry No.") { }
                    column(CustNo; "Cust No.")
                    {
                        ColumnFilter = CustNo = const('C1');
                    }
                    column(Amount; Amount) { }
                }
            }
        }

        codeunit 62483 "QCF 2418 Tests"
        {
            Subtype = Test;

            local procedure Seed()
            var
                Order: Record "QCF Order";
            begin
                Order.DeleteAll();
                Order.Init(); Order."Entry No." := 1; Order."Cust No." := 'C1'; Order.Amount := 100; Order.Insert();
                Order.Init(); Order."Entry No." := 2; Order."Cust No." := 'C1'; Order.Amount := -100; Order.Insert();
                Order.Init(); Order."Entry No." := 3; Order."Cust No." := 'C2'; Order.Amount := 50; Order.Insert();
            end;

            // C1's group total is 0 (100 + -100), which fails the static "> 0" ColumnFilter;
            // only C2 (total 50) may survive.
            [Test]
            procedure ColumnFilterOnSum_ExcludesZeroTotalGroup()
            var
                Q: Query "QCF Order Sum Filtered";
                RowCount: Integer;
            begin
                Seed();
                Q.Open();
                while Q.Read() do begin
                    RowCount += 1;
                    if Q.CustNo <> 'C2' then
                        Error('static ColumnFilter > 0 must keep only C2, got CustNo %1', Q.CustNo);
                    if Q.TotalAmount <> 50 then
                        Error('C2''s aggregated TotalAmount must be 50, got %1', Q.TotalAmount);
                end;
                Q.Close();

                if RowCount <> 1 then
                    Error('static ColumnFilter > 0 must keep exactly 1 group (C2), got %1 rows', RowCount);
            end;

            // A runtime SetFilter on the SAME aggregated column REPLACES the static
            // ColumnFilter: C1's total (0) fails the static "> 0" filter but satisfies the
            // runtime "<10" filter, so switching filters must bring C1 back and drop C2
            // (whose total 50 fails "<10").
            [Test]
            procedure ColumnFilterOnSum_RuntimeFilterReplacesStatic()
            var
                Q: Query "QCF Order Sum Filtered";
                RowCount: Integer;
                LastCust: Code[20];
                LastTotal: Decimal;
            begin
                Seed();
                Q.SetFilter(TotalAmount, '<%1', 10);
                Q.Open();
                while Q.Read() do begin
                    RowCount += 1;
                    LastCust := Q.CustNo;
                    LastTotal := Q.TotalAmount;
                end;
                Q.Close();

                if RowCount <> 1 then
                    Error('runtime filter must replace the static one, keeping exactly C1, got %1 rows', RowCount);
                if LastCust <> 'C1' then
                    Error('C1 must be the surviving group under the runtime filter, got %1', LastCust);
                if LastTotal <> 0 then
                    Error('C1''s group total must be 0 (100 + -100), got %1', LastTotal);
            end;

            // A static ColumnFilter on a PLAIN (non-aggregated) column is WHERE-style: it
            // drops raw rows before any grouping. Only C1's two rows may survive.
            [Test]
            procedure ColumnFilterOnPlainColumn_FiltersRawRows()
            var
                Q: Query "QCF Order Filtered";
                RowCount: Integer;
            begin
                Seed();
                Q.Open();
                while Q.Read() do begin
                    RowCount += 1;
                    if Q.CustNo <> 'C1' then
                        Error('static ColumnFilter const(C1) must keep only C1 rows, got CustNo %1', Q.CustNo);
                end;
                Q.Close();

                if RowCount <> 2 then
                    Error('both of C1''s raw rows must survive, none of C2''s; got %1 rows', RowCount);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void ColumnFilter_AppliesHavingAndWhereStyle_AndRuntimeFilterReplacesStatic()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, exitCode) = RunRunner(bundle);

        // Never silently pass a run that failed to even get the test codeunit compiled/run.
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // All three tests must have run and passed — 3P/0F/0E is TestExecutor's own per-bundle
        // summary line (see QueryHavingAndJoinAggregationProjectionTests for the same convention).
        Assert.Contains("3P/0F/0E", output);
    }
}
