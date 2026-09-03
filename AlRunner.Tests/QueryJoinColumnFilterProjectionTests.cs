using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2444 -- #2418 fixed a query column's static <c>ColumnFilter</c> property for the
/// single-dataitem path (<c>RecordPatches.QueryProjection.TranslateQueryFilters</c>, which
/// folds <c>NCLMetaQuery.ColumnFilters</c> into the same WHERE/HAVING routing already applied
/// to runtime <c>SetRange</c>/<c>SetFilter</c> filters). The multi-dataitem JOIN path had the
/// identical gap: <c>RecordPatches.QueryProjection.ApplyJoinRuntimeFilters</c> only read the
/// request's runtime <c>FiltersAndMarks</c>, never <c>NCLMetaQuery.ColumnFilters</c>, so a JOIN
/// query with a static <c>ColumnFilter</c> on any column -- aggregated or plain -- silently
/// ignored it. #2444 mirrors #2418's approach inside <c>ApplyJoinRuntimeFilters</c>: it now
/// reads the static filters via <c>GetStaticColumnFilters</c>, resolves each filtered column to
/// its join-projection slot via <c>ComputeJoinColumnSlotMap</c>, and evaluates it against the
/// already-projected join row -- skipping any column a runtime filter already targets on that
/// request (a runtime filter replaces the static one, not combines with it, per #2418's
/// verified real-BC behavior).
///
/// This is a RUNNER-MECHANISM test, not a claim about what real BC does -- same posture as
/// QueryColumnFilterProjectionTests.cs for #2418. The BEHAVIORAL claim (what BC itself does
/// with a static ColumnFilter on a joined query's aggregated vs. plain column, and that a
/// runtime filter on the same column replaces it) is adjudicated by the al-language corpus's
/// own CI against a real BC service tier
/// (StefanMaron/BusinessCentral.AL.Language.Tests, query/TestQueryJoin.al) -- the submodule pin
/// bump is a follow-up once that corpus PR merges, per .claude/rules/al-language-submodule.md.
/// This test exists so a regression in OUR OWN join-path ColumnFilter routing
/// (RecordPatches.QueryProjection.ApplyJoinRuntimeFilters / GetStaticColumnFilters /
/// ComputeJoinColumnSlotMap) fails loudly here without needing a full corpus run to notice.
///
/// Both scenarios are deliberately designed so a naive (wrong/no-op) implementation would
/// produce a DIFFERENT, distinguishable answer:
///   - The Sum scenario has a group whose total is exactly 0 (100 + -100) -- a no-op
///     ColumnFilter would let it through alongside the positive-total group, so the row COUNT
///     assertion alone fails if the filter is dropped.
///   - The plain-column scenario has rows for TWO customers across two joined tables -- a
///     no-op ColumnFilter would return both, so asserting only the filtered customer's rows
///     appear fails if dropped.
///   - The runtime-replaces-static scenario picks a runtime filter that the STATIC filter
///     alone would reject (C1's total is 0, failing "> 0") -- if the runtime filter were
///     merely ANDed with the static one instead of replacing it, the result would stay empty.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryJoinColumnFilterProjectionTests
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
        var root = Path.Combine(Path.GetTempPath(), "al-runner-query-join-columnfilter-2444", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "e9f3a6b4-2444-4c3d-ae5f-000000002444",
          "name": "QJCF 2444 Repro",
          "publisher": "Repro2444",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62500, "to": 62509 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "QjcfColumnFilter.al"), """
        table 62500 "Qjcf Header"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; "Cust No."; Code[20]) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }
        table 62501 "Qjcf Line"
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

        query 62502 "QJCF Join Sum Filtered"
        {
            QueryType = Normal;
            OrderBy = ascending(CustNo);
            elements
            {
                dataitem(Header; "Qjcf Header")
                {
                    column(CustNo; "Cust No.") { }
                    dataitem(Line; "Qjcf Line")
                    {
                        DataItemLink = "Header No." = Header."No.";
                        SqlJoinType = InnerJoin;
                        column(TotalAmount; Amount)
                        {
                            ColumnFilter = TotalAmount = filter(> 0);
                            Method = Sum;
                        }
                    }
                }
            }
        }

        query 62503 "QJCF Join Filtered"
        {
            QueryType = Normal;
            OrderBy = ascending(EntryNo);
            elements
            {
                dataitem(Header; "Qjcf Header")
                {
                    column(CustNo; "Cust No.")
                    {
                        ColumnFilter = CustNo = const('C1');
                    }
                    dataitem(Line; "Qjcf Line")
                    {
                        DataItemLink = "Header No." = Header."No.";
                        SqlJoinType = InnerJoin;
                        column(EntryNo; "Entry No.") { }
                        column(Amount; Amount) { }
                    }
                }
            }
        }

        codeunit 62504 "QJCF 2444 Tests"
        {
            Subtype = Test;

            local procedure Seed()
            var
                Header: Record "Qjcf Header";
                Line: Record "Qjcf Line";
            begin
                Header.DeleteAll();
                Line.DeleteAll();
                Header.Init(); Header."No." := 'H1'; Header."Cust No." := 'C1'; Header.Insert();
                Header.Init(); Header."No." := 'H2'; Header."Cust No." := 'C2'; Header.Insert();
                Line.Init(); Line."Entry No." := 1; Line."Header No." := 'H1'; Line.Amount := 100; Line.Insert();
                Line.Init(); Line."Entry No." := 2; Line."Header No." := 'H1'; Line.Amount := -100; Line.Insert();
                Line.Init(); Line."Entry No." := 3; Line."Header No." := 'H2'; Line.Amount := 50; Line.Insert();
            end;

            // C1's group total is 0 (100 + -100), which fails the static "> 0" ColumnFilter on
            // the joined Sum column; only C2 (total 50) may survive.
            [Test]
            procedure ColumnFilterOnJoinedSum_ExcludesZeroTotalGroup()
            var
                Q: Query "QJCF Join Sum Filtered";
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

            // A runtime SetFilter on the SAME aggregated join column REPLACES the static
            // ColumnFilter: C1's total (0) fails the static "> 0" filter but satisfies the
            // runtime "<10" filter, so switching filters must bring C1 back and drop C2
            // (whose total 50 fails "<10").
            [Test]
            procedure ColumnFilterOnJoinedSum_RuntimeFilterReplacesStatic()
            var
                Q: Query "QJCF Join Sum Filtered";
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

            // A static ColumnFilter on a PLAIN (non-aggregated) column of the driving dataitem
            // is WHERE-style: it drops raw joined rows before any grouping. Only C1's two
            // joined rows may survive.
            [Test]
            procedure ColumnFilterOnJoinedPlainColumn_FiltersRawRows()
            var
                Q: Query "QJCF Join Filtered";
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
                    Error('both of C1''s joined rows must survive, none of C2''s; got %1 rows', RowCount);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void ColumnFilter_AppliesOnJoinPath_HavingAndWhereStyle_AndRuntimeFilterReplacesStatic()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, exitCode) = RunRunner(bundle);

        // Never silently pass a run that failed to even get the test codeunit compiled/run.
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // All three tests must have run and passed — 3P/0F/0E is TestExecutor's own per-bundle
        // summary line (see QueryColumnFilterProjectionTests for the same convention).
        Assert.Contains("3P/0F/0E", output);
    }
}
