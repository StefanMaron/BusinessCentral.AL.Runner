using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2150 — a query column that declares BOTH a data source AND
/// <c>Method = Count</c> is rejected by real BC at compile time with AL0353
/// ("A Column must have a valid data source or have the 'Method' property set
/// to 'Count'"). The runner used to accept it and run the module anyway, so AL
/// that can never build against a real service tier compiled and ran here —
/// a test written and passing against the runner failed to compile the moment
/// it reached a real BC upstream (see corpus commit 4a7dde4d,
/// tests/al-language/tests/al-language/query/QjOrderSum.Query.al).
///
/// Root cause was NOT the diagnostic-collection mechanism BcCompiler.cs uses
/// (compilation.GetDeclarationDiagnostics() / emitResult.Diagnostics already
/// captures AL0353 correctly on every compile, unconditionally — verified by
/// direct decompilation of Microsoft.Dynamics.Nav.CodeAnalysis.dll:
/// SourceQueryColumnSymbol.CheckColumn() raises ERR_QueryColumnsMustDefine-
/// SourceExpressionOrCountMethod via AddDeclarationDiagnostics(), a
/// declaration-stage diagnostic, not a method-body one gated behind the more
/// expensive full-semantic-binding GetDiagnostics() call). The bug was in
/// Program.cs's consumption of that already-collected data: BC's own
/// ContinueBuildOnError keeps compiling an object's SIBLINGS after a
/// declaration-stage error on one of them, so `sources` can come back
/// non-empty (the broken query's metadata still emits) at the same time
/// `alDiagnostics` is also non-empty. Neither of Program.cs's two existing
/// guards fired for that combination — PARTIAL-EMIT-DROP requires
/// alDiagnostics.Count == 0, EMIT-ZERO requires sources.Count == 0 — so the
/// module silently ran with a real BC compile error in it.
///
/// This test spawns the real runner CLI (not BcCompiler in-process) because
/// the fix lives in Program.cs's post-emit gating, not in BcCompiler.Emit
/// itself — an in-process BcCompiler-level test would prove the diagnostic
/// exists but not that the CLI actually refuses to run the module.
/// </summary>
public class QueryColumnAlDiagnosticFailureTests
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

    private static string WriteBundle(string suffix, string queryBody)
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-al0353-" + suffix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "c2222222-2222-2222-2222-222222222222",
          "name": "AL0353 Diagnostic Test",
          "publisher": "Repro2150",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62200, "to": 62209 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Order.Table.al"), """
        table 62200 "AL0353 Order"
        {
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Amount; Decimal) { }
            }
            keys
            {
                key(PK; "No.") { Clustered = true; }
            }
        }
        """);
        File.WriteAllText(Path.Combine(root, "OrderSum.Query.al"), queryBody);
        return root;
    }

    [SkippableFact]
    public void Column_DeclaresDataSourceAndMethodCount_FailsCompileWithAl0353()
    {
        TestArtifacts.SkipIfMissing();

        // The invalid form real BC rejects: a column names BOTH a field AND
        // Method = Count. AL0353 fires because Count columns must count rows
        // in the group, not read a field.
        var root = WriteBundle("bad", """
        query 62201 "AL0353 Order Sum"
        {
            QueryType = Normal;

            elements
            {
                dataitem(Order; "AL0353 Order")
                {
                    column(TheAmount; Amount) { }
                    column(CountAmount; Amount) { Method = Count; }
                }
            }
        }
        """);

        var (output, exitCode) = RunRunner(root);

        // Would still pass if the runner always returned a default/no-op — assert
        // the SPECIFIC BC diagnostic, not just "something failed".
        Assert.NotEqual(0, exitCode);
        Assert.Contains("AL0353", output);
        Assert.Contains("A Column must have a valid data source or have the 'Method' property set to 'Count'", output);
    }

    [SkippableFact]
    public void Column_MethodCountWithNoDataSource_CompilesCleanly()
    {
        TestArtifacts.SkipIfMissing();

        // The corrected form real BC accepts: a Count column names NO field —
        // it counts rows in the group instead. Negative case above proves the
        // runner rejects bad AL; this proves the fix did not also reject valid
        // AL (a guard that always failed compilation would pass the test above
        // too, so this positive case is required to prove anything).
        var root = WriteBundle("good", """
        query 62202 "AL0353 Order Sum"
        {
            QueryType = Normal;

            elements
            {
                dataitem(Order; "AL0353 Order")
                {
                    column(TheAmount; Amount) { }
                    column(CountAmount) { Method = Count; }
                }
            }
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("AL0353", output);
    }
}
