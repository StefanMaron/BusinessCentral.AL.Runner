// TestCodeunitExecutionOrderTests — the runner must execute test codeunits in a defined
// order (#2801), and that order is ascending AL object ID.
//
// WHY THERE IS AN ORDER AT ALL. `TestExecutor.Run` took `assembly.GetTypes()` and walked it
// directly. .NET makes no promise about that array's order, so the runner's test-execution
// order was whatever BC's AL compiler happened to lay the types out as. On 2026-09-06 that
// order came out reversed on one CI leg (run 34016494342, BC 28.4.53241.54318): the two
// codeunits of the `SuiteAbortOnTimeoutTests` resume fixture ran Second-then-First, so the
// fixture's hang landed LAST, nothing was left for the watchdog abort to abandon, no resume
// was triggered, and three tests failed. The same commit's 27.5 leg ran the identical suite
// and passed. It was read as a watchdog that fired too late; the log shows the watchdog fired
// and the suite aborted exactly as designed. The order was the whole defect.
//
// WHY ASCENDING OBJECT ID, rather than name order or file order. This is Microsoft's order,
// not a preference. In the shipped Test Runner app (`Microsoft_Test Runner.app`,
// `src/src/TestSuiteMgt.Codeunit.al`), `GetTestMethods` iterates the codeunit inventory with
// a bare `FindSet()`/`Next()` and NO `SetCurrentKey`, so it walks primary-key order, handing
// each row to `AddTestMethod` with a monotonically increasing `Line No.`:
//
//     if CodeunitMetadata.FindSet() then
//         repeat
//             TestLineNo := GetLastTestLineNo(ALTestSuite) + 10000;
//             AddTestMethod(CodeunitMetadata, ALTestSuite, TestLineNo);
//         until CodeunitMetadata.Next() = 0;
//
// `AddTestMethod` stores `TestMethodLine."Test Codeunit" := CodeunitMetadata.ID`, and every
// consumer then reads the lines back with `SetCurrentKey("Line No."); Ascending(true)`. The
// pre-CLEAN27 overload does the same over `AllObjWithCaption`, whose primary key is
// ("Object Type", "Object ID"). Both inventories are keyed on the object ID, so a BC test
// suite runs its codeunits in ascending object ID.
//
// WHAT THIS TEST PROVES, and why it cannot pass by luck. The fixture names its files so that
// ordinal FILE order is the exact REVERSE of object-ID order — Aardvark=62295, Middle=62290,
// Zulu=62285. File order is what reaches the AL compiler (SafeDirectoryScan.Files sorts
// ordinally, #2892), so an implementation that does not reorder produces DESCENDING ids here,
// deterministically. The assertions below are written as the RULE ("the ids that ran are in
// ascending order"), not as a hardcoded sequence that an unsorted implementation might satisfy
// on a lucky day.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestCodeunitExecutionOrderTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>`PASS  Codeunit62290.MidDeclaredFirst (0ms)` -> ("62290", "MidDeclaredFirst").</summary>
    private static readonly Regex RanLine = new(
        @"^\s*PASS\s+Codeunit(?<id>\d+)\.(?<method>\w+)\s", RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly string _root;

    public TestCodeunitExecutionOrderTests()
    {
        _root = TestScratch.Dir("al-runner-codeunit-order");
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Three codeunits whose FILE name order is the reverse of their OBJECT ID order, so
    /// "did the runner reorder anything?" has a visible answer. Each declares two [Test]
    /// procedures whose declaration order is the reverse of their alphabetical order, which
    /// is how the method-level guarantee below stays distinguishable from a blanket sort.
    /// </summary>
    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "b7c8d9e0-f1a2-3456-7890-abcdef123456",
          "name": "Codeunit Execution Order Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62280, "to": 62299 } ],
          "runtime": "14.0"
        }
        """);

        // Sorts FIRST by file name, highest object ID.
        File.WriteAllText(Path.Combine(dir, "Aardvark.Codeunit.al"), """
        codeunit 62295 "Order Probe High"
        {
            Subtype = Test;

            [Test]
            procedure ZetaDeclaredFirst()
            begin
            end;

            [Test]
            procedure AlphaDeclaredSecond()
            begin
            end;
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Middle.Codeunit.al"), """
        codeunit 62290 "Order Probe Mid"
        {
            Subtype = Test;

            [Test]
            procedure ZetaDeclaredFirst()
            begin
            end;

            [Test]
            procedure AlphaDeclaredSecond()
            begin
            end;
        }
        """);

        // Sorts LAST by file name, lowest object ID.
        File.WriteAllText(Path.Combine(dir, "Zulu.Codeunit.al"), """
        codeunit 62285 "Order Probe Low"
        {
            Subtype = Test;

            [Test]
            procedure ZetaDeclaredFirst()
            begin
            end;

            [Test]
            procedure AlphaDeclaredSecond()
            begin
            end;
        }
        """);
    }

    /// <summary>
    /// Positive, and the claim of the whole file: the codeunits ran in ascending object ID.
    ///
    /// Stated as the rule — the observed id sequence equals its own ascending sort — so it
    /// says what the contract is rather than restating one sequence. The fixture makes it
    /// unsatisfiable by an implementation that does not reorder: file order is the reverse
    /// of id order, so leaving `assembly.GetTypes()` alone yields 62295, 62290, 62285.
    /// </summary>
    [SkippableFact]
    public void TestCodeunits_RunInAscendingObjectIdOrder_NotFileOrder()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(_root);
        Assert.Equal(0, exit);

        var ids = CodeunitIdsInExecutionOrder(output);
        Assert.Equal(new[] { 62285, 62290, 62295 }, ids);
        Assert.True(ids.SequenceEqual(ids.OrderBy(x => x)),
            "test codeunits must execute in ascending AL object ID (BC's own suite order — see "
            + "TestSuiteMgt.Codeunit.al). Got: [" + string.Join(", ", ids) + "]. The fixture's FILE "
            + "order is the reverse of its id order, so a descending result means nothing reordered "
            + "the types and the runner is still executing in Assembly.GetTypes() order."
            + "\n--- runner output ---\n" + output);
    }

    /// <summary>
    /// Negative — ordering must not change WHAT runs. A sort that dropped a codeunit, or
    /// duplicated one, would still be "ascending" and would still pass the test above.
    /// Six tests, three codeunits, two per codeunit, every one of them passing.
    /// </summary>
    [SkippableFact]
    public void Ordering_DoesNotChangeWhichTestsRun()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(_root);
        Assert.Equal(0, exit);

        var ran = RanInExecutionOrder(output);
        Assert.Equal(6, ran.Count);
        Assert.Equal(6, ran.Distinct().Count());
        Assert.Equal(
            new[]
            {
                "62285.AlphaDeclaredSecond", "62285.ZetaDeclaredFirst",
                "62290.AlphaDeclaredSecond", "62290.ZetaDeclaredFirst",
                "62295.AlphaDeclaredSecond", "62295.ZetaDeclaredFirst",
            },
            ran.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Contains("pass:        6", output);
    }

    /// <summary>
    /// Negative — the fix must order CODEUNITS only. Methods inside a codeunit keep their
    /// source-declaration order, which `OrderTestMethodsBySourceDeclaration` has guaranteed
    /// since before this change. Each codeunit declares `ZetaDeclaredFirst` before
    /// `AlphaDeclaredSecond`, so a blanket alphabetical or id-derived sort that reached the
    /// method level would invert every pair here and fail.
    /// </summary>
    [SkippableFact]
    public void MethodsWithinACodeunit_KeepSourceDeclarationOrder()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner(_root);
        Assert.Equal(0, exit);

        var ran = RanInExecutionOrder(output);
        Assert.Equal(
            new[]
            {
                "62285.ZetaDeclaredFirst", "62285.AlphaDeclaredSecond",
                "62290.ZetaDeclaredFirst", "62290.AlphaDeclaredSecond",
                "62295.ZetaDeclaredFirst", "62295.AlphaDeclaredSecond",
            },
            ran);
    }

    /// <summary>
    /// The order is a property of the runner, not of one lucky process. Three separate runner
    /// processes, each with its own cold cache directory, must all produce the same sequence —
    /// which is the actual defect #2801 describes (a sequence that changed between processes on
    /// the same commit and the same BC build).
    /// </summary>
    [SkippableFact]
    public void ExecutionOrder_IsStableAcrossProcesses()
    {
        TestArtifacts.SkipIfMissing();

        var sequences = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var cache = Path.Combine(_root, "..", $"cache-{Guid.NewGuid():N}");
            var (output, exit) = RunRunner(_root, $"--cache \"{Path.GetFullPath(cache)}\"");
            Assert.Equal(0, exit);
            sequences.Add(string.Join(",", RanInExecutionOrder(output)));
            try { Directory.Delete(Path.GetFullPath(cache), recursive: true); } catch { }
        }

        Assert.Single(sequences.Distinct());
        Assert.StartsWith("62285.", sequences[0], StringComparison.Ordinal);
    }

    private static List<int> CodeunitIdsInExecutionOrder(string output)
    {
        var ids = new List<int>();
        foreach (Match m in RanLine.Matches(output))
        {
            var id = int.Parse(m.Groups["id"].Value);
            if (ids.Count == 0 || ids[^1] != id) ids.Add(id);
        }
        return ids;
    }

    private static List<string> RanInExecutionOrder(string output) =>
        RanLine.Matches(output)
            .Select(m => $"{m.Groups["id"].Value}.{m.Groups["method"].Value}")
            .ToList();

    private (string output, int exit) RunRunner(string bundle, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundle}\"");
        foreach (var a in extraArgs) args.Append($" {a}");
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
}
