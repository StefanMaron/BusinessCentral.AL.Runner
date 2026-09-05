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
// declares the HIGHER id (62242) and Zeta.Codeunit.al the lower (62241). So a fix that
// accidentally sorted by object id, or one that left creation order in place, produces a
// different answer than the one asserted — the test cannot pass by coincidence.
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
    /// and it is the ordinal order of the source file paths (Alpha before Zeta), not the
    /// order the files were created in and not the order of the object ids.
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

        var expected = new[] { "Codeunit62242.AlphaOnly", "Codeunit62241.ZetaOnly" };
        Assert.True(orderA.SequenceEqual(expected),
            "the bundle whose Zeta.Codeunit.al was created FIRST must still run Alpha's codeunit "
            + "first — source-path ordinal order, not creation order. Got: ["
            + string.Join(", ", orderA) + "]\n--- runner output ---\n" + outA);
        Assert.True(orderB.SequenceEqual(expected),
            "the bundle whose Alpha.Codeunit.al was created FIRST must run in the same order as "
            + "the other one. Got: [" + string.Join(", ", orderB) + "]\n--- runner output ---\n" + outB);
    }
}
