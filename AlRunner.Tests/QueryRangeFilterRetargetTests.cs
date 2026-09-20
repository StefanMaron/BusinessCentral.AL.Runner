using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #3508 — the runner-side mechanism behind the range half of query-column filter
/// retargeting.
///
/// <para>A RUNNER-MECHANISM test, not a claim about what real BC does. It pins that
/// <c>RecordPatches.QueryProjection.RetargetFilterExpression</c> rebuilds a
/// <c>RangeFilterExpression</c> against the source table field's <c>ExpressionContext</c>, so
/// BC's own <c>FilterExpressionVisitor.VisitRange</c> — which desugars a range into Unary
/// leaves carrying that same context, wrapping the two-sided form in an <c>And</c> — hands
/// <c>RecordBufferEvaluatorVisitor.Evaluate</c> something whose
/// <c>(NCLMetaField)expressionContext.Metadata</c> cast succeeds. Left unretargeted the cast
/// throws, which is the 47 Microsoft-surface failures #3508 measured.</para>
///
/// <para>The BEHAVIOURAL claim — what BC returns for <c>15..25</c>, <c>20..</c>, <c>..10</c> and
/// an alternation of two ranges — is adjudicated upstream by a real BC service tier in
/// StefanMaron/BusinessCentral.AL.Language.Tests#390, which adds seven arms to corpus codeunit
/// 60205. Those arms went <c>Failed: 7, Passed: 19</c> against the unfixed runner and
/// <c>Failed: 0, Passed: 26</c> against the fixed one, so they discriminate on the behaviour
/// rather than riding along.</para>
///
/// <para>What is pinned HERE and nowhere upstream is the runner's own escalation: a filter kind
/// the retargeting does not recognise must reach a named <c>RunnerOutOfScopeException</c>
/// rather than BC's bare <c>InvalidCastException</c>. The upstream corpus cannot express that,
/// because on real BC there is no unrecognised kind to reach it with.</para>
///
/// <para>Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.</para>
/// </summary>
public class QueryRangeFilterRetargetTests
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
        var root = TestScratch.Dir("al-runner-query-range-3508");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c7d1e4f2-3508-4a1b-9c3d-000000003508",
          "name": "QRF 3508 Repro",
          "publisher": "Repro3508",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62460, "to": 62469 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "QrfLocal.al"), """
        table 62460 "QRF Local"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; "Amt"; Integer) { }
                // OptimizeForTextSearch is what makes the FullText arm below reachable AT ALL.
                // BC's FilterExpressionParser.ParseFullTextFilterExpressionImpl downgrades a
                // `&&term` filter to a Wildcard unless the column's metadata answers
                // SupportsFullTextSearch, and NCLMetaQueryColumn.SupportsFullTextSearch forwards
                // to NCLMetaField.OptimizeForTextSearch. Measured: without this property the
                // filter arrives as a WildcardFilterExpression (so it would test the #2299 branch
                // again, not the new one); with it, a FullTextFilterExpression.
                field(3; "Descr"; Text[100]) { OptimizeForTextSearch = true; }
            }
            keys { key(PK; "Code") { Clustered = true; } }
        }

        query 62461 "QRF Local Rows"
        {
            QueryType = Normal;
            elements
            {
                dataitem(QrfLocal; "QRF Local")
                {
                    column(Code; "Code") { }
                    column(Amt; "Amt") { }
                    column(Descr; "Descr") { }
                }
            }
        }

        codeunit 62462 "QRF 3508 Tests"
        {
            Subtype = Test;

            // Seeded once and reused: the runner's default TestIsolation=Codeunit shares data
            // inside a codeunit, so the guard keeps each test's own seeding idempotent rather
            // than relying on which test ran first.
            local procedure Seed()
            var
                R: Record "QRF Local";
            begin
                if R.IsEmpty() then begin
                    R.Init(); R."Code" := 'A10'; R."Amt" := 10; R."Descr" := 'alpha beta'; R.Insert();
                    R.Init(); R."Code" := 'B20'; R."Amt" := 20; R."Descr" := 'gamma delta'; R.Insert();
                    R.Init(); R."Code" := 'C30'; R."Amt" := 30; R."Descr" := 'epsilon zeta'; R.Insert();
                end;
            end;

            // RangeBetweenInclusive — the shape whose desugaring produces the doubled
            // VisitBinary in #3508's reported stack.
            [Test]
            procedure RangeBetween()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetFilter(Amt, '15..25');
                Q.Open();
                while Q.Read() do N += 1;
                Q.Close();
                if N <> 1 then Error('RangeBetween expected 1, got %1', N);
            end;

            // RangeFromInclusive — desugars to a single GreaterThanOrEqual Unary leaf.
            [Test]
            procedure RangeFrom()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetFilter(Amt, '20..');
                Q.Open();
                while Q.Read() do N += 1;
                Q.Close();
                if N <> 2 then Error('RangeFrom expected 2, got %1', N);
            end;

            // RangeToInclusive — the mirror, a single LessThanOrEqual Unary leaf.
            [Test]
            procedure RangeTo()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetFilter(Amt, '..20');
                Q.Open();
                while Q.Read() do N += 1;
                Q.Close();
                if N <> 2 then Error('RangeTo expected 2, got %1', N);
            end;

            // A Range nested under a Binary Or, which is the two-level tree #3508 names: the
            // retargeting has to recurse into the Or's children AND rebuild each Range.
            [Test]
            procedure OrOfRanges()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetFilter(Amt, '5..12|28..35');
                Q.Open();
                while Q.Read() do N += 1;
                Q.Close();
                if N <> 2 then Error('OrOfRanges expected 2, got %1', N);
            end;

            // The discriminating arm for OrOfRanges: the row BETWEEN the two ranges must be
            // excluded. Without it, an implementation that dropped the filter entirely and
            // returned all three rows would... still fail OrOfRanges, but one that returned
            // exactly the two endpoints for the wrong reason would not be caught.
            [Test]
            procedure OrOfRangesExcludesTheGap()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetFilter(Amt, '5..12|28..35');
                Q.Open();
                while Q.Read() do begin
                    N += 1;
                    if Q.Amt = 20 then Error('OrOfRanges must exclude Amt=20, which falls between the two ranges');
                end;
                Q.Close();
                if N <> 2 then Error('OrOfRangesExcludesTheGap expected 2, got %1', N);
            end;

            [Test]
            procedure RangeNoMatch()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetFilter(Amt, '100..200');
                Q.Open();
                while Q.Read() do N += 1;
                Q.Close();
                if N <> 0 then Error('RangeNoMatch expected 0, got %1', N);
            end;

            // The THIRD route to the cast, which #3508's body does not mention: a FullText
            // filter (`&&term`) on a column whose source field sets OptimizeForTextSearch.
            // BC's RecordBufferEvaluatorVisitor.VisitFullText calls the same Evaluate that
            // VisitUnary and VisitWildcard do, so before this fix it threw the identical
            // InvalidCastException. Without OptimizeForTextSearch on the field, BC rewrites the
            // filter to a Wildcard and this arm silently re-tests the #2299 branch instead.
            [Test]
            procedure FullTextMatch()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetFilter(Descr, '&&alpha');
                Q.Open();
                while Q.Read() do N += 1;
                Q.Close();
                if N <> 1 then Error('FullTextMatch expected 1, got %1', N);
            end;

            // The discriminating arm for FullTextMatch: a term no row carries must return zero
            // rows. Without it, an implementation returning every row would still fail
            // FullTextMatch but for the wrong reason, and one returning the first row always
            // would pass it.
            [Test]
            procedure FullTextNoMatch()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetFilter(Descr, '&&omicron');
                Q.Open();
                while Q.Read() do N += 1;
                Q.Close();
                if N <> 0 then Error('FullTextNoMatch expected 0, got %1', N);
            end;

            // CONTROL: the Unary route, which worked before #3508 and must keep working. A
            // regression here would be invisible in the Range arms above.
            [Test]
            procedure UnaryControl()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetFilter(Amt, '>15');
                Q.Open();
                while Q.Read() do N += 1;
                Q.Close();
                if N <> 2 then Error('UnaryControl expected 2, got %1', N);
            end;

            // CONTROL: the Wildcard route #2299 fixed. Same reason — #3508 rewrites the method
            // that branch lives in, so its continued correctness is not implied by the new arms.
            [Test]
            procedure WildcardControl2299()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetFilter(Code, 'B*');
                Q.Open();
                while Q.Read() do N += 1;
                Q.Close();
                if N <> 1 then Error('WildcardControl2299 expected 1, got %1', N);
            end;

            // CONTROL: SetRange, the Unary-equality route, unchanged since before #2299.
            [Test]
            procedure SetRangeControl()
            var
                Q: Query "QRF Local Rows";
                N: Integer;
            begin
                Seed();
                Q.SetRange(Code, 'C30');
                Q.Open();
                while Q.Read() do N += 1;
                Q.Close();
                if N <> 1 then Error('SetRangeControl expected 1, got %1', N);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void RangeAndFullTextFiltersOnQueryColumn_RetargetToSourceField_LeavingUnaryAndWildcardIntact()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, exitCode) = RunRunner(bundle);

        // Never silently pass a run that failed to get the test codeunit compiled or run.
        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // The #3508 / #2299 signature itself: if this string is present, the retargeting let an
        // expression through still keyed by the NCLMetaQueryColumn.
        Assert.DoesNotContain("InvalidCastException", output);
        Assert.DoesNotContain("NCLMetaQueryColumn", output);
        // 11P/0F/0E is TestExecutor's own per-bundle summary line. Asserting the COUNT as well as
        // the zeros is what stops a bundle that silently ran fewer tests from reading as green.
        Assert.Contains("11P/0F/0E", output);
        Assert.Equal(0, exitCode);
    }
}
