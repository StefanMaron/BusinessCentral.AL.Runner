using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2299 — <c>Query.SetFilter(&lt;column&gt;, 'ABC*')</c> (a wildcard filter on a query
/// column) crashed at the first <c>Read()</c> with <c>InvalidCastException: Unable to cast
/// object of type 'NCLMetaQueryColumn' to type 'NCLMetaField'</c>. <c>SetRange</c> on the same
/// column worked, because BC's own <c>TempTableDataProvider.RecordBufferEvaluatorVisitor
/// .Evaluate</c> casts <c>expressionContext.Metadata</c> to <c>NCLMetaField</c> for every
/// non-Unary/Binary filter-expression leaf (wildcard, full-text, ...), and
/// <c>RecordPatches.QueryProjection.RetargetFilterExpression</c> only retargeted Unary/Binary
/// expressions from the query column's <c>ExpressionContext</c> to its source table field's —
/// a <c>WildcardFilterExpression</c> was returned unretargeted, still keyed by the
/// NCLMetaQueryColumn.
///
/// This is a RUNNER-MECHANISM test, not a claim about what real BC does: it pins that our own
/// filter-retargeting step now covers the Wildcard shape, not just Unary/Binary. The BEHAVIORAL
/// claim (real BC evaluates a wildcard filter on a query column against the column's source
/// field, same as Record.SetFilter) is proven upstream against a live BC service tier — see
/// StefanMaron/BusinessCentral.AL.Language.Tests, per docs/rules/bc-behavior-tests-go-upstream.md.
///
/// The fixture also exercises the sibling defect the same repro surfaced: [Test] procedure
/// declaration-order preservation (#1766) broke whenever one test's name is a strict prefix of
/// another's in the same codeunit (e.g. "Wildcard" / "Wildcard_NoMatch") — TestExecutor's
/// nested-scope-type lookup used an end-anchored-only regex, so the longer name's scope type
/// could match the shorter name's lookup too, and FirstOrDefault picked whichever came first in
/// reflection order instead of the correct one. That silently reordered these tests relative to
/// AL source order, letting one test's committed row leak into an earlier-declared test's result
/// under the runner's default TestIsolation=Codeunit (data is intentionally NOT rolled back
/// between tests in the same codeunit — see docs/limitations.md's "Test isolation modes").
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryWildcardFilterProjectionTests
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
        var root = Path.Combine(Path.GetTempPath(), "al-runner-query-wildcard-2299", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c7d1e4f2-2299-4a1b-9c3d-000000002299",
          "name": "QWF 2299 Repro",
          "publisher": "Repro2299",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62450, "to": 62459 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "QwfLocal.al"), """
        table 62450 "QWF Local"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Code"; Code[20]) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        query 62451 "QWF Local Rows"
        {
            QueryType = Normal;
            elements
            {
                dataitem(QwfLocal; "QWF Local")
                {
                    column(Code; "Code") { }
                }
            }
        }

        codeunit 62452 "QWF 2299 Tests"
        {
            Subtype = Test;

            var
                Log: Text;

            // Both procedure names deliberately share the "SetFilterWildcard" prefix — the
            // shorter name is a strict prefix of the longer one, the exact shape that broke
            // #1766's declaration-order preservation (see class doc comment). Declared in
            // this order (Wildcard, then Wildcard_NoMatch, then SetRange_Control) so a
            // regression in either the wildcard retargeting OR the ordering fix reproduces as
            // a wrong row count here, under the runner's real (undocumented-away)
            // TestIsolation=Codeunit no-rollback-between-tests default.
            [Test]
            procedure SetFilterWildcard()
            var
                QwfLocal: Record "QWF Local";
                LocalRows: Query "QWF Local Rows";
                RowCount: Integer;
            begin
                Log += 'Wildcard;';
                QwfLocal.Init();
                QwfLocal."Code" := 'LW1';
                QwfLocal.Insert();

                LocalRows.SetFilter(Code, 'LW*');
                LocalRows.Open();
                while LocalRows.Read() do
                    RowCount += 1;
                LocalRows.Close();

                if RowCount <> 1 then
                    Error('Order=%1 Expected 1 row (only this test''s own LW1), got %2', Log, RowCount);
            end;

            [Test]
            procedure SetFilterWildcard_NoMatch()
            var
                QwfLocal: Record "QWF Local";
                LocalRows: Query "QWF Local Rows";
                RowCount: Integer;
            begin
                Log += 'NoMatch;';
                QwfLocal.Init();
                QwfLocal."Code" := 'LW2';
                QwfLocal.Insert();

                LocalRows.SetFilter(Code, 'ZZ*');
                LocalRows.Open();
                while LocalRows.Read() do
                    RowCount += 1;
                LocalRows.Close();

                if RowCount <> 0 then
                    Error('Order=%1 Expected 0 rows, got %2', Log, RowCount);
            end;

            [Test]
            procedure SetRangeControl()
            var
                QwfLocal: Record "QWF Local";
                LocalRows: Query "QWF Local Rows";
                RowCount: Integer;
            begin
                Log += 'Range;';
                QwfLocal.Init();
                QwfLocal."Code" := 'LW3';
                QwfLocal.Insert();

                LocalRows.SetRange(Code, 'LW3');
                LocalRows.Open();
                while LocalRows.Read() do
                    RowCount += 1;
                LocalRows.Close();

                if RowCount <> 1 then
                    Error('Order=%1 Expected 1 row, got %2', Log, RowCount);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void WildcardFilterOnQueryColumn_MatchesSourceField_InSourceDeclarationOrder()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, exitCode) = RunRunner(bundle);

        // Never silently pass a run that failed to even get the test codeunit compiled/run.
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        Assert.DoesNotContain("InvalidCastException", output);
        // All three tests must have run and passed — 3P/0F/0E is TestExecutor's own
        // per-bundle summary line (see CrossBundleModuleIdentityDedupTests for the same
        // convention).
        Assert.Contains("3P/0F/0E", output);
    }
}
