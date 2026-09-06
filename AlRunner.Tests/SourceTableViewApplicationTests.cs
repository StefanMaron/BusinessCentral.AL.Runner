using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2820 — a page's <c>SourceTableView</c> was not applied when the runner opened it.
///
/// BC applies it in <c>NavForm.OpenFormAsync</c>, which calls
/// <c>ApplySourceTableViewAndSavedValuesAsync()</c> and only then
/// <c>RaiseOnOpenPageAsync()</c>. The runner's TestPage machinery does not run
/// <c>OpenFormAsync</c> — it drives a page's lifecycle triggers directly — so on that route
/// nothing called <c>ApplySourceTableView</c>: the page showed rows its own view excludes,
/// and <c>Rec.GetFilter(...)</c> inside <c>FilterGroup(2)</c>, where BC puts view filters,
/// answered blank. (A modal page AL runs itself takes a different route —
/// <c>RunnerModalDispatch.TryOpenForm</c> calls BC's own <c>NavForm.OpenForm()</c>, which
/// applies the view itself. That route was broken for the other half's reason: a precompiled
/// dependency page carried no view in its synthesized metadata for BC to apply.)
///
/// <para>The reported symptom was one page's consequence rather than the defect itself. Base
/// Application page 7016 "Sales Price List" declares
/// <c>SourceTableView = where("Price Type" = const(Sale))</c>, and its OnOpenPage reaches
/// codeunit 7018 "Price UX Management".GetFirstSourceFromFilter, which ends with
/// <c>Evaluate(PriceSource."Price Type", PriceListHeader.GetFilter("Price Type"))</c> inside
/// FilterGroup(2). With the view unapplied that GetFilter returned <c>''</c>, and evaluating
/// <c>''</c> into enum "Price Type" (Any,Sale,Purchase — no blank member) threw
/// NavNCLInvalidOptionStringException. The blank was never the bug; the missing filter was.</para>
///
/// <para>This is a RUNNER-MECHANISM test: it pins that the runner's own page-open path calls
/// BC's ApplySourceTableView, on a page the runner source-compiles itself. The BEHAVIOURAL
/// claim underneath it — that BC filters a page's rows by its SourceTableView, puts those
/// filters in filter group 2 and applies <c>sorting(...) order(...)</c> — is plain BC
/// behaviour and is proved upstream against a real service tier
/// (StefanMaron/BusinessCentral.AL.Language.Tests, per
/// .claude/rules/bc-behavior-tests-go-upstream.md). The precompiled-dependency half of the fix
/// (reconstructing <c>&lt;SourceTableView&gt;</c> from a dependency .app's
/// SymbolReference.json) is pinned by DependencyPageMetadataXmlTests, which needs no BC
/// artifact.</para>
///
/// <para>The fixture deliberately declares no <c>application</c> — nothing here needs a Base
/// Application object (see .claude/rules/no-base-app-in-csharp-tests.md).</para>
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class SourceTableViewApplicationTests
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
        var root = TestScratch.Dir("al-runner-source-table-view-2820");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "8d31c0b6-2820-4f77-9a15-000000002820",
          "name": "STV 2820 Repro",
          "publisher": "Repro2820",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62480, "to": 62489 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "StvRow.al"), """
        table 62480 "STV Row"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[10]) { }
                field(2; Bucket; Integer) { }
                field(3; Kind; Enum "STV Kind") { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }
        """);
        // The page writes what it saw at open time into a row of this table, because a
        // TestPage exposes controls and actions only — a page procedure is not reachable
        // from AL through a TestPage variable, so a filter-group observation has to travel
        // through the database.
        File.WriteAllText(Path.Combine(root, "StvProbe.al"), """
        table 62481 "STV Probe"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; "Bucket Filter G2"; Text[100]) { }
                field(3; "Kind Filter G2"; Text[100]) { }
                field(4; "Bucket Filter G0"; Text[100]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """);
        // No blank member — the same shape as Base Application's enum 7009 "Price Type",
        // whose Any/Sale/Purchase members are what the reported failure named.
        File.WriteAllText(Path.Combine(root, "StvKind.al"), """
        enum 62480 "STV Kind"
        {
            Extensible = true;
            value(0; Any) { }
            value(1; Sale) { }
            value(2; Purchase) { }
        }
        """);
        File.WriteAllText(Path.Combine(root, "StvList.al"), """
        page 62480 "STV List"
        {
            PageType = List;
            SourceTable = "STV Row";
            SourceTableView = sorting(Bucket, "No.")
                              order(descending)
                              where(Bucket = filter(1|2), Kind = const(Purchase));

            layout
            {
                area(content)
                {
                    repeater(Rows)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                        field(Bucket; Rec.Bucket) { ApplicationArea = All; }
                    }
                }
            }

            trigger OnOpenPage()
            var
                Probe: Record "STV Probe";
            begin
                Probe.Init();
                Probe."Entry No." := 1;
                Probe."Bucket Filter G0" := CopyStr(Rec.GetFilter(Bucket), 1, 100);
                Rec.FilterGroup(2);
                Probe."Bucket Filter G2" := CopyStr(Rec.GetFilter(Bucket), 1, 100);
                Probe."Kind Filter G2" := CopyStr(Rec.GetFilter(Kind), 1, 100);
                Rec.FilterGroup(0);
                Probe.Insert();
            end;
        }
        """);
        File.WriteAllText(Path.Combine(root, "StvTests.al"), """
        codeunit 62482 "STV 2820 Tests"
        {
            Subtype = Test;

            local procedure Seed()
            var
                Row: Record "STV Row";
            begin
                Row.DeleteAll();
                // In the view:      A (Bucket 1, Purchase), B (Bucket 2, Purchase)
                // Out of the view:  C (wrong Kind), D (Bucket outside 1|2)
                Row.Init(); Row."No." := 'A'; Row.Bucket := 1; Row.Kind := Row.Kind::Purchase; Row.Insert();
                Row.Init(); Row."No." := 'B'; Row.Bucket := 2; Row.Kind := Row.Kind::Purchase; Row.Insert();
                Row.Init(); Row."No." := 'C'; Row.Bucket := 2; Row.Kind := Row.Kind::Sale; Row.Insert();
                Row.Init(); Row."No." := 'D'; Row.Bucket := 3; Row.Kind := Row.Kind::Purchase; Row.Insert();
            end;

            [Test]
            procedure SourceTableViewFiltersRowsAndSortsThemAsDeclared()
            var
                L: TestPage "STV List";
            begin
                Seed();
                L.OpenView();

                // sorting(Bucket, "No.") order(descending) — under the table's own primary
                // key (ascending "No.") the first row would be A, so this pins the sorting
                // as well as the filtering.
                if not L.First() then
                    Error('the view must leave rows A and B visible, but the page has no rows');
                if L."No.".Value <> 'B' then
                    Error('expected B first (highest Bucket, descending), got %1', L."No.".Value);
                if not L.Next() then
                    Error('expected a second visible row (A), got none');
                if L."No.".Value <> 'A' then
                    Error('expected A second, got %1', L."No.".Value);

                // …and nothing else. C fails Kind = const(Purchase), D fails
                // Bucket = filter(1|2); an unapplied view would show all four.
                if L.Next() then
                    Error('expected exactly 2 visible rows, but a third (%1) is reachable', L."No.".Value);
            end;

            [Test]
            procedure SourceTableViewFiltersLandInFilterGroup2AndNotInTheUserGroup()
            var
                L: TestPage "STV List";
                Probe: Record "STV Probe";
            begin
                Seed();
                Probe.DeleteAll();
                L.OpenView();

                if not Probe.Get(1) then
                    Error('OnOpenPage did not run, so nothing was observed');
                // Exactly what Base Application page 7016 reads out of FilterGroup(2) and
                // hands to Evaluate — blank here is the reported failure.
                if Probe."Bucket Filter G2" <> '1|2' then
                    Error('expected FilterGroup(2) Bucket filter ''1|2'', got ''%1''', Probe."Bucket Filter G2");
                if Probe."Kind Filter G2" <> 'Purchase' then
                    Error('expected FilterGroup(2) Kind filter ''Purchase'', got ''%1''', Probe."Kind Filter G2");
                // The negative half: a view filter belongs to group 2 only. Leaking it into
                // group 0 would be visible to AL that reads the user filter pane, and would
                // survive a FilterGroup(0) SetRange/Reset the view must not.
                if Probe."Bucket Filter G0" <> '' then
                    Error('view filter leaked into FilterGroup(0): ''%1''', Probe."Bucket Filter G0");
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void PageOpen_AppliesSourceTableView_FilteringSortingAndInFilterGroup2()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, _) = RunRunner(bundle);

        // Never silently pass a run that failed to get the test codeunit compiled/run.
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // Both tests must have run and passed — TestExecutor's own per-bundle summary line.
        Assert.Contains("2P/0F/0E", output);
    }
}
