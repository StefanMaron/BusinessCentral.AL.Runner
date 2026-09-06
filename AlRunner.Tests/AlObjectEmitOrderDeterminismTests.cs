// AlObjectEmitOrderDeterminismTests — the end-to-end half of #2872.
//
// SafeDirectoryScanOrderTests pins the unit-level contract (SafeDirectoryScan returns paths in
// ordinal order). This pins what that contract is FOR: two bundles holding byte-identical AL
// sources must run the same tests in the same order, whatever order their files happened to be
// created in.
//
// The chain the bug ran down: SafeDirectoryScan.Files -> BcCompiler.Emit's `alFiles` -> the
// syntax-tree array handed to the AL compiler -> the emitted assembly's TypeDef order ->
// Assembly.GetTypes() -> the `for (int ti = 0; ti < types.Length; ti++)` loop in
// TestExecutor.Run. Nothing sorts anywhere along it, and readdir on Linux is creation order for
// a small directory, so creating Second.Codeunit.al before First.Codeunit.al ran the second
// codeunit first.
//
// Measured before the fix, same runner build, same machine, identical AL bytes:
//   files created First-then-Second -> Codeunit62241 ... then Codeunit62242
//   files created Second-then-First -> Codeunit62242 ... then Codeunit62241
//
// Harmless-looking until something ends the run early. On BC 27.5 in run 33984312053 a
// watchdog abort ended the run at the hung codeunit (TestExecutor's `return results`), so the
// flipped order decided WHICH tests had already run, and SuiteAbortOnTimeoutTests' three
// order-sensitive assertions failed on `main` — with `AL emit: 0.0s`, i.e. every one of them
// served the same wrongly-ordered assembly out of one cache entry.
//
// The file names here are deliberately anti-correlated with the object IDs: Alpha.Codeunit.al
// declares the HIGHER id (62242) and Zeta.Codeunit.al the lower (62241). So creation order,
// file-name order and object-id order are three different answers here, and the test cannot
// pass by coincidence whichever one the runner implements.
//
// #2801 UPDATE — the expected order changed, the claim did not. This file used to assert that
// the answer was ordinal FILE order. That was the right observation about #2872's fix and the
// wrong contract: sorting the compiler's inputs does not pin the output, because
// Assembly.GetTypes() has no defined order either. Measured against a three-codeunit fixture
// whose ordinal file order is 62295, 62290, 62285, GetTypes() returned 62295, 62285, 62290 —
// neither file order nor id order — and on CI run 34016494342 the SuiteAbortOnTimeoutTests
// fixture flipped again on a build that already carried #2872's sort. TestExecutor now orders
// test codeunits by ascending AL object ID, which is the order a real BC test suite runs
// (TestSuiteMgt.GetTestMethods walks the codeunit inventory in primary-key order; see
// TestCodeunitExecutionOrderTests for the full citation). So the expectation below is
// 62241 before 62242.
//
// What this file proves is unchanged and is now stronger: identical AL content must run in one
// order regardless of how the files were created — and, since that order is no longer derived
// from the file names, regardless of what they are called.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlObjectEmitOrderDeterminismTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public AlObjectEmitOrderDeterminismTests()
    {
        _root = TestScratch.Dir("al-runner-emit-order");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string AppJson = """
    {
      "id": "b1c2d3e4-f5a6-4708-9901-2233445566aa",
      "name": "AL Emit Order Determinism Fixture",
      "publisher": "AL Runner",
      "version": "1.0.0.0",
      "dependencies": [],
      "platform": "1.0.0.0",
      "idRanges": [ { "from": 62240, "to": 62249 } ],
      "runtime": "14.0"
    }
    """;

    private const string ZetaAl = """
    codeunit 62241 "Emit Order Zeta"
    {
        Subtype = Test;

        [Test]
        procedure ZetaOnly()
        begin
        end;
    }
    """;

    private const string AlphaAl = """
    codeunit 62242 "Emit Order Alpha"
    {
        Subtype = Test;

        [Test]
        procedure AlphaOnly()
        begin
        end;
    }
    """;

    /// <summary>
    /// Writes the fixture into a fresh subdirectory, creating the two .al files in the
    /// requested order. On ext4/tmpfs a directory this small lists in creation order, which is
    /// exactly the lever the bug turned.
    /// </summary>
    private string WriteBundle(string name, bool zetaFirst)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), AppJson);
        if (zetaFirst)
        {
            File.WriteAllText(Path.Combine(dir, "Zeta.Codeunit.al"), ZetaAl);
            File.WriteAllText(Path.Combine(dir, "Alpha.Codeunit.al"), AlphaAl);
        }
        else
        {
            File.WriteAllText(Path.Combine(dir, "Alpha.Codeunit.al"), AlphaAl);
            File.WriteAllText(Path.Combine(dir, "Zeta.Codeunit.al"), ZetaAl);
        }
        return dir;
    }

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundle}\"");
        // --no-cache so the AL output is really re-emitted for each bundle. A cache HIT would
        // serve one assembly to both and hide the very difference under test — which is how
        // this reached `main`: the CI failure's own log reads "AL emit: 0.0s".
        args.Append(" --no-cache");
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
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>The executed test names, in the order the runner reported them.</summary>
    private static List<string> ExecutionOrder(string output)
        => Regex.Matches(output, @"^PASS\s+(\S+)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();

    /// <summary>
    /// Positive: identical AL content, opposite file-creation order, one execution order —
    /// and it is ascending AL object ID (Zeta's 62241 before Alpha's 62242), not the order the
    /// files were created in and not the ordinal order of their names (#2801).
    /// </summary>
    [SkippableFact]
    public void SameSources_DifferentFileCreationOrder_RunInTheSameOrder()
    {
        TestArtifacts.SkipIfMissing();

        var zetaFirst = WriteBundle("zeta-first", zetaFirst: true);
        var alphaFirst = WriteBundle("alpha-first", zetaFirst: false);

        var (outA, exitA) = RunRunner(zetaFirst);
        var (outB, exitB) = RunRunner(alphaFirst);

        Assert.Equal(0, exitA);
        Assert.Equal(0, exitB);

        var orderA = ExecutionOrder(outA);
        var orderB = ExecutionOrder(outB);

        // Both tests actually ran in both bundles — otherwise "same order" is trivially true.
        Assert.Equal(2, orderA.Count);
        Assert.Equal(2, orderB.Count);

        var expected = new[] { "Codeunit62241.ZetaOnly", "Codeunit62242.AlphaOnly" };
        Assert.True(orderA.SequenceEqual(expected),
            "the bundle whose Zeta.Codeunit.al was created FIRST must run codeunit 62241 before "
            + "62242 — ascending object id, not creation order and not file-name order. Got: ["
            + string.Join(", ", orderA) + "]\n--- runner output ---\n" + outA);
        Assert.True(orderB.SequenceEqual(expected),
            "the bundle whose Alpha.Codeunit.al was created FIRST must run in the same order as "
            + "the other one. Got: [" + string.Join(", ", orderB) + "]\n--- runner output ---\n" + outB);
    }
}
