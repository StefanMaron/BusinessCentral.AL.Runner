// Issue #2152 — the AL-diagnostic compile-failure guard #2150/#2154 added only
// covered the default bundled compile path. `--per-suite` compiles one module per
// SUITE (see the `else` branch of Program.cs's bundled/non-bundled run loop split,
// around EnumerateSuites) but shares the exact same BC ContinueBuildOnError shape:
// a declaration-stage error on one object (e.g. a query column declaring both a
// data source AND `Method = Count`, AL0353) does not stop BC from compiling that
// object's siblings, so `sources` can come back non-empty at the same time
// `suiteAlDiagnostics` is also non-empty. Before this fix, --per-suite had no gate
// at all for that combination and silently ran (and could pass) a module a real BC
// service tier would refuse to publish.
//
// This spawns the real runner CLI with `--per-suite` (not BcCompiler in-process),
// because the fix lives in Program.cs's post-emit gating for that specific code
// path, not in BcCompiler.Emit itself.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public class PerSuiteAlDiagnosticFailureTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --per-suite");
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

    // A directory with app.json at its OWN root is one suite (LooksLikeSuite), so
    // `--per-suite` against this root compiles exactly one module — the shape this
    // test needs to isolate the per-suite gate from the bundled one.
    private static string WriteBundle(string suffix, string queryBody)
    {
        var root = Path.Combine(Path.GetTempPath(), "al-runner-persuite-al0353-" + suffix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "d3333333-3333-3333-3333-333333333333",
          "name": "Per-Suite AL0353 Diagnostic Test",
          "publisher": "Repro2152",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62210, "to": 62219 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(root, "Order.Table.al"), """
        table 62210 "AL0353 PS Order"
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
    public void PerSuite_ColumnDeclaresDataSourceAndMethodCount_FailsCompileWithAl0353()
    {
        TestArtifacts.SkipIfMissing();

        var root = WriteBundle("bad", """
        query 62211 "AL0353 PS Order Sum"
        {
            QueryType = Normal;

            elements
            {
                dataitem(Order; "AL0353 PS Order")
                {
                    column(TheAmount; Amount) { }
                    column(CountAmount; Amount) { Method = Count; }
                }
            }
        }
        """);

        var (output, exitCode) = RunRunner(root);

        // Would still pass if --per-suite always returned a default/no-op — assert
        // the SPECIFIC BC diagnostic, not just "something failed".
        Assert.NotEqual(0, exitCode);
        Assert.Contains("AL0353", output);
        Assert.Contains("A Column must have a valid data source or have the 'Method' property set to 'Count'", output);
    }

    [SkippableFact]
    public void PerSuite_ColumnMethodCountWithNoDataSource_CompilesCleanly()
    {
        TestArtifacts.SkipIfMissing();

        // The corrected form real BC accepts: proves the per-suite gate does not also
        // reject valid AL — a guard that always failed compilation would pass the
        // negative test above too, so this positive case is required to prove anything.
        var root = WriteBundle("good", """
        query 62212 "AL0353 PS Order Sum"
        {
            QueryType = Normal;

            elements
            {
                dataitem(Order; "AL0353 PS Order")
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
