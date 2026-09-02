using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2423 -- a multi-real-dataitem JOIN query that ALSO selects a FlowField column
/// silently read the column's typed default (observed: 0) instead of the calculated value,
/// once #2295 unblocked the query's own metadata construction. #2300 fixed the
/// single-real-dataitem FlowField case (via NCLMetaQueryDataItem.SourceFlowField and
/// FlowFieldPatches.CalcOneFlowFieldForQueryRow), but deliberately left the join projection
/// path (AlRunner.QueryJoin.JoinExecutor.BuildJoinProjectionPlan and its al-runner-side
/// mirror RecordPatches.QueryProjection.ComputeJoinColumnSlotMap) unwired for a FlowField
/// column: both still resolve the FlowField sub-dataitem's own aggregate column via a
/// generic TableSlot read against the SOURCE table (the FlowField's own source, not any
/// table the join actually reads a row buffer for), which silently produced the column's
/// typed default rather than either the real value or an error.
///
/// This is a RUNNER-MECHANISM test pinning the loud-failure guard #2423's own investigation
/// added: BuildJoinProjectionPlan/ComputeJoinColumnSlotMap now throw
/// RunnerOutOfScopeException (reason query-join-flowfield-column-not-implemented, naming the
/// synthesized sub-dataitem and "see #2423") the moment they encounter that sub-dataitem,
/// instead of silently defaulting the column -- a silent wrong answer is exactly what
/// .claude/rules/loud-failures.md forbids. Implementing the real join+FlowField computation
/// is tracked in #2423 itself, not this test.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class QueryJoinFlowFieldColumnOosTests
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
        var root = Path.Combine(Path.GetTempPath(), "al-runner-query-join-flowfield-oos-2423", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c7d1e4f2-2423-4a1b-9c3d-000000002423",
          "name": "QJF 2423 Repro",
          "publisher": "Repro2423",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62470, "to": 62479 } ],
          "runtime": "14.0"
        }
        """);
        // Entirely application-local (no dependency needed): "Qjf Link" joins to "Qjf Header",
        // whose "Total Amount" is a FlowField summing "Qjf Line" -- the same shape as #2300's
        // JoinIleFlowFieldColumn_ReadsCalculatedValue (a local table inner-joined to a table
        // whose FlowField column is selected), just without the Base Application dependency a
        // C# fixture app.json may not declare.
        File.WriteAllText(Path.Combine(root, "QjfLine.al"), """
        table 62470 "Qjf Line"
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
        File.WriteAllText(Path.Combine(root, "QjfHeader.al"), """
        table 62471 "Qjf Header"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; "Total Amount"; Decimal)
                {
                    FieldClass = FlowField;
                    CalcFormula = sum("Qjf Line".Amount where("Header No." = field("No.")));
                }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }
        """);
        File.WriteAllText(Path.Combine(root, "QjfLink.al"), """
        table 62472 "Qjf Link"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; "Header No."; Code[20]) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }
        """);
        File.WriteAllText(Path.Combine(root, "QjfJoinFlowField.al"), """
        query 62473 "QJF Join FlowField"
        {
            QueryType = Normal;
            elements
            {
                dataitem(QjfLink; "Qjf Link")
                {
                    column(LinkEntryNo; "Entry No.") { }
                    dataitem(QjfHeader; "Qjf Header")
                    {
                        DataItemLink = "No." = QjfLink."Header No.";
                        SqlJoinType = InnerJoin;
                        column(TotalAmount; "Total Amount") { }
                    }
                }
            }
        }
        """);
        File.WriteAllText(Path.Combine(root, "QjfTests.al"), """
        codeunit 62474 "QJF 2423 Tests"
        {
            Subtype = Test;

            [Test]
            procedure JoinWithFlowFieldColumn_ThrowsOutOfScope_InsteadOfReadingZeroSilently()
            var
                QjfHeader: Record "Qjf Header";
                QjfLine: Record "Qjf Line";
                QjfLink: Record "Qjf Link";
                Q: Query "QJF Join FlowField";
                Cost: Decimal;
            begin
                QjfHeader.Init(); QjfHeader."No." := 'H1'; QjfHeader.Insert();
                QjfLine.Init(); QjfLine."Entry No." := 1; QjfLine."Header No." := 'H1'; QjfLine.Amount := 7.25; QjfLine.Insert();
                QjfLink.Init(); QjfLink."Entry No." := 1; QjfLink."Header No." := 'H1'; QjfLink.Insert();

                Q.Open();
                if not Q.Read() then
                    Error('expected one row');
                Cost := Q.TotalAmount;
                Q.Close();
                // If the runner ever silently returns a value here instead of throwing, this
                // assertion catches the exact #2423 corruption directly: the pre-guard
                // behavior was reading 0 (the column's typed default) instead of 7.25.
                if Cost <> 7.25 then
                    Error('Expected the runner to throw RunnerOutOfScopeException (see #2423), not silently read %1', Cost);
            end;
        }
        """);

        return root;
    }

    [SkippableFact]
    public void QueryJoinWithFlowFieldColumn_ThrowsLoudOutOfScope_InsteadOfSilentlyDefaultingToZero()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteBundle();
        var (output, exitCode) = RunRunner(bundle);

        Assert.DoesNotContain("EMIT-EXCLUDED", output);
        Assert.DoesNotContain("COMPILE FAIL", output);
        // The decisive assertions: the test FAILS at runtime because
        // RunnerOutOfScopeException propagates before the AL Error() call could ever fire,
        // and the failure carries the #2423 loud-failure guard's own reason string -- not a
        // silently-wrong AL-level "Expected the runner..." message, and not any unrelated
        // crash.
        Assert.Contains("query-join-flowfield-column-not-implemented", output);
        Assert.Contains("see #2423", output);
        Assert.DoesNotContain("Expected the runner to throw", output);
        Assert.Contains("0P/1F/0E", output);
    }
}
