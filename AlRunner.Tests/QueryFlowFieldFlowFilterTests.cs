using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2925 -- a query column backed by a FlowField whose CalcFormula carries a
/// FieldClass.FlowFilter where-condition used to throw a NullReferenceException out of BC's
/// own FlowFieldsHelper.GetFilterFromMetaFilterCollection.
///
/// The null is BC's, but the fault is the runner's. Decompiling that method (bc281,
/// Microsoft.Dynamics.Nav.Runtime.FlowFieldsHelper) shows exactly one unguarded dereference of
/// its filtersAndMarks parameter, in the FieldClass.FlowFilter branch:
///
///     case FieldClass.FlowFilter:
///         filterExpression = GetFlowFilterBasedFilter(nCLMetaFilterField, filtersAndMarks.Filters, session);
///
/// FlowFieldPatches.CalcOneFlowFieldForQueryRow entered the shared FlowField core with
/// parentFiltersAndMarks hard-coded to null, so every CalcFormula with a flow-filter condition
/// crashed on the query path while the identical formula computed fine on the Record.CalcFields
/// path (which supplies the record's real FiltersAndMarks).
///
/// Two things are pinned here, and the second is why the first is not enough on its own:
///
///   1. The crash is gone. Passing BC's own FiltersAndMarks.Empty would do that much -- its
///      Filters field is null (verified from the .cctor IL: `new FiltersAndMarks(null, null)`),
///      and GetFlowFilterBasedFilter answers a null dictionary with "no constraint".
///   2. A flow filter the QUERY actually sets still reaches the calculation. Fixing only (1)
///      turns the crash into a silently wrong number: the runner measured 15 where the flow
///      filter says 14, because TranslateQueryFilters was pushing the filter down as a row
///      predicate on a FlowFilter field -- a field with no stored value to filter rows on --
///      instead of routing it to the CalcFormula. That is the loud-failures.md silent default,
///      one layer up.
///
/// The BEHAVIORAL claim (what value real BC computes in each case) is proven upstream against a
/// real service tier -- StefanMaron/BusinessCentral.AL.Language.Tests PR #175, per
/// .claude/rules/bc-behavior-tests-go-upstream.md. This suite pins the runner MECHANISM: that
/// the query-projection path and the multi-dataitem JOIN path both route the query's own flow
/// filters into FlowFieldPatches.CalcOneFlowFieldForQueryRow, and that neither crashes.
///
/// The join case is in the same bundle deliberately. Before this fix it failed with the SAME
/// NRE, but reported at RecordPatches.QueryJoin.cs:204 with no BC frame at all, because
/// ExecuteJoinQuery rethrew with `throw tie.InnerException` -- which resets the stack trace.
/// That is why issue #2925 describes it as a possibly-unrelated second cluster. The rethrow now
/// goes through ExceptionDispatchInfo, so the origin survives.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryFlowFieldFlowFilterTests
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
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
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
        var root = TestScratch.Dir("al-runner-query-flowfilter-2925");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c7d1e4f2-2925-4a1b-9c3d-000000002925",
          "name": "QFF 2925 Repro",
          "publisher": "Repro2925",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62470, "to": 62479 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "Line.al"), """
        table 62470 "QFF25 Line"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; "Header No."; Code[20]) { }
                field(3; "Posting Date"; Date) { }
                field(4; Amount; Decimal) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """);

        // "Total Amount" narrows its aggregate with the "Date Filter" flow filter -- the
        // FieldClass.FlowFilter where-condition that reaches the unguarded dereference.
        File.WriteAllText(Path.Combine(root, "Header.al"), """
        table 62471 "QFF25 Header"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; "Date Filter"; Date) { FieldClass = FlowFilter; }
                field(3; "Total Amount"; Decimal)
                {
                    FieldClass = FlowField;
                    CalcFormula = sum("QFF25 Line".Amount where("Header No." = field("No."),
                                                                "Posting Date" = field("Date Filter")));
                }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }
        """);

        File.WriteAllText(Path.Combine(root, "Tag.al"), """
        table 62472 "QFF25 Tag"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Header No."; Code[20]) { }
                field(2; "Tag Code"; Code[20]) { }
            }
            keys { key(PK; "Header No.", "Tag Code") { Clustered = true; } }
        }
        """);

        // No filter() element at all -- the query cannot set the flow filter, so BC's own
        // "unset flow filter contributes no condition" answer must apply.
        File.WriteAllText(Path.Combine(root, "QPlain.al"), """
        query 62473 "QFF25 Plain"
        {
            QueryType = Normal;
            elements
            {
                dataitem(H; "QFF25 Header")
                {
                    column(No; "No.") { }
                    column(TotalAmount; "Total Amount") { }
                }
            }
        }
        """);

        File.WriteAllText(Path.Combine(root, "QFiltered.al"), """
        query 62474 "QFF25 Filtered"
        {
            QueryType = Normal;
            elements
            {
                dataitem(H; "QFF25 Header")
                {
                    column(No; "No.") { }
                    filter(DateFilter; "Date Filter") { }
                    column(TotalAmount; "Total Amount") { }
                }
            }
        }
        """);

        // Same flow-filtered FlowField column, reached through the multi-dataitem JOIN path
        // (AlRunner.QueryJoin.JoinExecutor) rather than the single-dataitem projection path.
        File.WriteAllText(Path.Combine(root, "QJoin.al"), """
        query 62475 "QFF25 Join Filtered"
        {
            QueryType = Normal;
            elements
            {
                dataitem(H; "QFF25 Header")
                {
                    column(No; "No.") { }
                    filter(DateFilter; "Date Filter") { }
                    column(TotalAmount; "Total Amount") { }

                    dataitem(T; "QFF25 Tag")
                    {
                        DataItemLink = "Header No." = H."No.";
                        column(TagCode; "Tag Code") { }
                    }
                }
            }
        }
        """);

        // Three lines: 10 on 2024-01-10, 4 on 2024-02-10, 1 on 2024-03-10. Whole aggregate 15;
        // the 2024-01-01..2024-02-15 window is 14. 14 and 15 are both non-zero and different
        // from each other, so neither a dropped filter nor a default-returning implementation
        // can produce the expected value by accident.
        File.WriteAllText(Path.Combine(root, "Tests.al"), """
        codeunit 62476 "QFF25 Tests"
        {
            Subtype = Test;

            local procedure Seed()
            var
                H: Record "QFF25 Header";
                L: Record "QFF25 Line";
                T: Record "QFF25 Tag";
            begin
                if not H.Get('H1') then begin
                    H.Init(); H."No." := 'H1'; H.Insert();
                end;
                if T.IsEmpty() then begin
                    T.Init(); T."Header No." := 'H1'; T."Tag Code" := 'A'; T.Insert();
                end;
                if L.IsEmpty() then begin
                    L.Init(); L."Entry No." := 1; L."Header No." := 'H1'; L."Posting Date" := 20240110D; L.Amount := 10; L.Insert();
                    L.Init(); L."Entry No." := 2; L."Header No." := 'H1'; L."Posting Date" := 20240210D; L.Amount := 4; L.Insert();
                    L.Init(); L."Entry No." := 3; L."Header No." := 'H1'; L."Posting Date" := 20240310D; L.Amount := 1; L.Insert();
                end;
            end;

            [Test]
            procedure RecordCalcFields_NoFlowFilter_SumsAll()
            var
                H: Record "QFF25 Header";
            begin
                Seed();
                H.Get('H1');
                H.CalcFields("Total Amount");
                if H."Total Amount" <> 15 then
                    Error('rec-nofilter: expected 15, got %1', H."Total Amount");
            end;

            [Test]
            procedure RecordCalcFields_WithFlowFilter_SumsRange()
            var
                H: Record "QFF25 Header";
            begin
                Seed();
                H.Get('H1');
                H.SetRange("Date Filter", 20240101D, 20240215D);
                H.CalcFields("Total Amount");
                if H."Total Amount" <> 14 then
                    Error('rec-filter: expected 14, got %1', H."Total Amount");
            end;

            [Test]
            procedure QueryFlowFieldColumn_NoFlowFilter_SumsAll()
            var
                Q: Query "QFF25 Plain";
                T: Decimal;
            begin
                Seed();
                Q.SetRange(No, 'H1');
                Q.Open();
                if not Q.Read() then Error('q1: expected one row');
                T := Q.TotalAmount;
                Q.Close();
                if T <> 15 then Error('q1: expected 15, got %1', T);
            end;

            [Test]
            procedure QueryFlowFieldColumn_WithFlowFilter_SumsRange()
            var
                Q: Query "QFF25 Filtered";
                T: Decimal;
            begin
                Seed();
                Q.SetRange(No, 'H1');
                Q.SetFilter(DateFilter, '%1..%2', 20240101D, 20240215D);
                Q.Open();
                if not Q.Read() then Error('q2: expected one row');
                T := Q.TotalAmount;
                Q.Close();
                if T <> 14 then Error('q2: expected 14, got %1', T);
            end;

            [Test]
            procedure JoinQueryFlowFieldColumn_NoFlowFilter_SumsAll()
            var
                Q: Query "QFF25 Join Filtered";
                T: Decimal;
            begin
                Seed();
                Q.SetRange(No, 'H1');
                Q.Open();
                if not Q.Read() then Error('q3a: expected one row');
                T := Q.TotalAmount;
                if Q.TagCode <> 'A' then Error('q3a: expected tag A, got %1', Q.TagCode);
                Q.Close();
                if T <> 15 then Error('q3a: expected 15, got %1', T);
            end;

            [Test]
            procedure JoinQueryFlowFieldColumn_WithFlowFilter_SumsRange()
            var
                Q: Query "QFF25 Join Filtered";
                T: Decimal;
            begin
                Seed();
                Q.SetRange(No, 'H1');
                Q.SetFilter(DateFilter, '%1..%2', 20240101D, 20240215D);
                Q.Open();
                if not Q.Read() then Error('q3b: expected one row');
                T := Q.TotalAmount;
                if Q.TagCode <> 'A' then Error('q3b: expected tag A, got %1', Q.TagCode);
                Q.Close();
                if T <> 14 then Error('q3b: expected 14, got %1', T);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void QueryFlowFieldColumn_WithFlowFilterCondition_CalculatesInsteadOfCrashing()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, exitCode) = RunRunner(bundle);

        // Never silently pass a run that never got the test codeunit compiled and executed.
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // The crash this issue reports, by the BC frame it appeared in.
        Assert.DoesNotContain("GetFilterFromMetaFilterCollection", output);
        Assert.DoesNotContain("NullReferenceException", output);
        // 6P/0F/0E is TestExecutor's own per-bundle summary line. All six must have run: two
        // Record.CalcFields controls (the path that always worked, so a regression there shows
        // as a failure rather than as a silently-changed expectation), two single-dataitem
        // query cases, and two JOIN cases.
        Assert.Contains("6P/0F/0E", output);
        Assert.Equal(0, exitCode);
    }
}
