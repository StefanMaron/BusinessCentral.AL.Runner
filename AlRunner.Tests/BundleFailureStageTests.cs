// BundleFailureStageTests — issue #2779.
//
// THE DEFECT THESE PIN
//   A bundle that produced zero tests was reported as COMPILE FAIL unconditionally, whatever
//   actually failed. Measured on the ms-bucket workflow's first run (Actions run 33967273260):
//   every AL object in Microsoft's Tests-ERM bucket compiled cleanly, the BC backup reader then
//   refused the backup at run time, and the report said `compile-fail: 1`, `exec-fail: 0`,
//   printed "=== Tests-ERM — COMPILE FAIL ===" and classified the failure `compile/other` in
//   --out's JSON. Reading that report sends you hunting for AL compile errors that do not
//   exist, which is exactly what happened.
//
//   Two halves have to hold together, and the second is what makes the first safe:
//     1. the STAGE is decided by what failed (BundleFailureStage), and
//     2. every consumer of BucketStage.ExecuteFailed actually PRINTS the reason. `ProcessError`
//        is set only by the out-of-process fan-out path, so before this change an in-process
//        execution failure would have rendered as a bare "EXEC FAIL" header with no text under
//        it — trading a wrong report for an empty one.
//
//   The drift guard at the bottom is the third: it reads Program.cs's own error literals, so a
//   new marker that nothing classifies fails the build instead of silently defaulting.
using System.Text.RegularExpressions;
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class BundleFailureStageTests
{
    // ───────────────────────────────────────────────── marker parsing ──

    [Theory]
    [InlineData("Tests-ERM: EXEC-FAIL: the backup reader failed (exit 1)", "EXEC-FAIL")]
    [InlineData("<bundled>: EMIT-TIMEOUT after 3600s", "EMIT-TIMEOUT")]
    [InlineData("<bundled>: COMPILE-FAIL (3): CS0246 …", "COMPILE-FAIL")]
    [InlineData("Contoso_Sales_1_0_0_0: TEST-TIMEOUT-ABORT: codeunit 50000 abandoned", "TEST-TIMEOUT-ABORT")]
    [InlineData("<bundled>: PARTIAL-EMIT-DROP for M: 4 object(s) declared", "PARTIAL-EMIT-DROP")]
    public void MarkerOf_ReadsTheMarkerOffTheRealLineShapes(string line, string expected)
        => Assert.Equal(expected, BundleFailureStage.MarkerOf(line));

    [Theory]
    [InlineData("")]
    [InlineData("no separator at all")]
    [InlineData("name: lowercase text with no marker")]
    public void MarkerOf_ReturnsNullWhenThereIsNoMarker(string line)
        => Assert.Null(BundleFailureStage.MarkerOf(line));

    // ───────────────────────────────────────────────── stage decision ──

    /// <summary>The CI failure this issue is about, at the level the stage is decided.</summary>
    [Fact]
    public void AReaderFailureAtRunTimeIsAnExecutionFailure_NotACompileFailure()
    {
        var errors = new[]
        {
            "Tests-ERM: EXEC-FAIL: the backup reader failed (exit 1): block 116504 of MSDA region "
            + "is neither mapped by the derived extent list nor padding filler",
        };

        Assert.Equal(BucketStage.ExecuteFailed, BundleFailureStage.Classify(errors));
    }

    [Fact]
    public void CodeunitsAbandonedByTheWatchdogAreAnExecutionFailure()
        => Assert.Equal(BucketStage.ExecuteFailed,
            BundleFailureStage.Classify(new[] { "App: TEST-TIMEOUT-ABORT: codeunit 50000 abandoned" }));

    /// <summary>The negative direction, and the one that keeps this from being a rename: a real
    /// compile failure must still read as one.</summary>
    [Theory]
    [InlineData("<bundled>: COMPILE-FAIL (3): CS0246 The type or namespace 'X' could not be found")]
    [InlineData("<bundled>: EMIT-FAIL: BC's compiler threw")]
    [InlineData("<bundled>: EMIT-ZERO (7 AL error(s))")]
    [InlineData("Suite: AL-DIAGNOSTIC-FAIL (2): AL0118 …")]
    [InlineData("<bundled>: EMIT-TIMEOUT after 120s")]
    [InlineData("<bundled>: EMIT-EXCLUDED for M: 2 object(s) dropped from the module")]
    [InlineData("<bundled>: PARTIAL-EMIT-DROP for M: 9 object(s) declared, only 4 emitted")]
    public void EveryPreExecutionMarkerStillReadsAsACompileFailure(string error)
        => Assert.Equal(BucketStage.CompileFailed, BundleFailureStage.Classify(new[] { error }));

    /// <summary>A bundle where one app group failed to compile and another threw at run time is
    /// a compile failure: something genuinely did not compile, and reporting "exec fail" would
    /// be the same wrong-report defect pointing the other way.</summary>
    [Fact]
    public void AMixOfCompileAndExecutionErrorsStaysACompileFailure()
    {
        var errors = new[]
        {
            "AppA: COMPILE-FAIL (1): CS0103 The name 'x' does not exist",
            "AppB: EXEC-FAIL: session is not open",
        };

        Assert.Equal(BucketStage.CompileFailed, BundleFailureStage.Classify(errors));
    }

    /// <summary>An unrecognised marker is conservative, not optimistic — the CompileFailed path
    /// is the one that prints the whole error list, so a line nobody classified is still read.</summary>
    [Fact]
    public void AnUnrecognisedMarkerFallsBackToCompileFailed()
        => Assert.Equal(BucketStage.CompileFailed,
            BundleFailureStage.Classify(new[] { "App: SOME-NEW-MARKER: whatever" }));

    [Fact]
    public void NoErrorsAtAllIsNotAnExecutionFailure()
        => Assert.Equal(BucketStage.CompileFailed, BundleFailureStage.Classify(Array.Empty<string>()));

    // ─────────────────────────────────── the reason must be printed ──

    private static BucketResult ExecFailedBucket(params string[] errors)
        => new("/tmp/ms-buckets/Tests-ERM", BucketStage.ExecuteFailed, errors, null,
               Array.Empty<TestResult>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>
    /// The half that makes the stage split safe. `ProcessError` is null for an in-process
    /// failure, and the EXEC FAIL branch used to print only that — so switching the stage
    /// without this would have replaced a wrong header with an empty block.
    /// </summary>
    [Fact]
    public void PrintPerTest_PrintsTheReasonUnderTheExecFailHeader()
    {
        var w = new StringWriter();
        Reporter.PrintPerTest(new[] { ExecFailedBucket(
            "Tests-ERM: EXEC-FAIL: the backup reader failed (exit 1): block 116504 of MSDA region") },
            w, showPass: false);

        var text = w.ToString();
        Assert.Contains("— EXEC FAIL ===", text, StringComparison.Ordinal);
        Assert.Contains("block 116504 of MSDA region", text, StringComparison.Ordinal);
        Assert.DoesNotContain("COMPILE FAIL", text, StringComparison.Ordinal);
    }

    /// <summary>Same claim for the JSON `--out` writes: results.json carried
    /// `"kind": "compile"` and `"classification": "compile/other"` for this exact failure.</summary>
    [Fact]
    public void WriteClassification_NamesItAnExecutionFailureAndCarriesTheReason()
    {
        var dir = TestScratch.Dir("al-runner-2779-classification");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "results.json");
        Reporter.WriteClassification(new[] { ExecFailedBucket(
            "Tests-ERM: EXEC-FAIL: the backup reader failed (exit 1): block 116504 of MSDA region") },
            path);

        var json = File.ReadAllText(path);
        Assert.Contains("\"kind\": \"execute\"", json, StringComparison.Ordinal);
        Assert.Contains("execute/exec-fail", json, StringComparison.Ordinal);
        Assert.Contains("block 116504 of MSDA region", json, StringComparison.Ordinal);
        Assert.DoesNotContain("compile/other", json, StringComparison.Ordinal);
    }

    /// <summary>An ExecuteFailed bucket used to appear NOWHERE in --json: not in `tests` (it has
    /// none) and not in `compilationErrors` (it is not compile-failed). A whole bundle vanished
    /// from the document with nothing saying so.</summary>
    [Fact]
    public void SerializeJsonOutput_DoesNotDropAnExecutionFailedBundle()
    {
        var json = Reporter.SerializeJsonOutput(
            new[] { ExecFailedBucket("Tests-ERM: EXEC-FAIL: the backup reader failed (exit 1): block 116504") }, 2);

        Assert.Contains("executionErrors", json, StringComparison.Ordinal);
        Assert.Contains("block 116504", json, StringComparison.Ordinal);
    }

    /// <summary>Negative: a run with no execution failure serialises exactly as before, so the
    /// new field is additive rather than a schema change every consumer has to absorb.</summary>
    [Fact]
    public void SerializeJsonOutput_OmitsTheFieldWhenNothingFailedToExecute()
    {
        var ran = new BucketResult("/tmp/ok", BucketStage.Ran, Array.Empty<string>(), null,
            Array.Empty<TestResult>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

        Assert.DoesNotContain("executionErrors", Reporter.SerializeJsonOutput(new[] { ran }, 0),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The interaction the stage split would otherwise have broken. `--jobs` fan-out counts the
    /// per-bundle headers in each shard's captured output to report "NOT RUN: N bundle(s)"
    /// (#2715 / #2721) — a bundle with zero tests is absent from that shard's JUnit, so without
    /// the count the aggregate reports a smaller total as though it were complete. It looked for
    /// the COMPILE FAIL header only; moving execution failures to their own header would have
    /// re-introduced exactly that silent loss.
    ///
    /// The counter is fed the REAL printer's output here, so the two cannot drift apart by
    /// someone spelling the header twice.
    /// </summary>
    [Fact]
    public void FanOutCountsAnExecutionFailedBundleAsNotRun_JustLikeACompileFailedOne()
    {
        var w = new StringWriter();
        Reporter.PrintPerTest(new[] { ExecFailedBucket("Tests-ERM: EXEC-FAIL: the reader failed") },
            w, showPass: false);
        var shardOutput = w.ToString();

        var counted = Infrastructure.ParallelFanOut.NotRunHeaders
            .Sum(h => Infrastructure.ParallelFanOut.CountOccurrences(shardOutput, h));
        Assert.Equal(1, counted);

        // The pre-fix behaviour, stated as a fact: looking only for the compile header scores
        // this bundle 0 — absent from the aggregate with nothing saying so.
        Assert.Equal(0, Infrastructure.ParallelFanOut.CountOccurrences(shardOutput, " — COMPILE FAIL ==="));
    }

    // ────────────────────────────────────────────────── drift guard ──

    /// <summary>
    /// Every marker Program.cs and ExecFailure.cs actually write must be classified here. Without
    /// this, adding a marker and forgetting to classify it produces a silently mislabelled report
    /// — the same class of defect as the one being fixed, and invisible until someone reads a
    /// summary and believes it.
    /// </summary>
    [Fact]
    public void EveryMarkerTheRunnerWritesIsClassified()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var token = new Regex(@"\b[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+\b");

        var found = new SortedSet<string>(StringComparer.Ordinal);

        // Program.cs: the `bundleErrors.Add(...)` call and the three lines that may continue it.
        var program = File.ReadAllLines(Path.Combine(repoRoot, "AlRunner", "Program.cs"));
        for (var i = 0; i < program.Length; i++)
        {
            if (!program[i].Contains("bundleErrors.Add", StringComparison.Ordinal)) continue;
            for (var w = i; w < Math.Min(i + 4, program.Length); w++)
                foreach (Match m in token.Matches(program[w]))
                    found.Add(m.Value);
        }

        // ExecFailure.cs composes the EXEC-FAIL line for the bundled path, so its literal is not
        // in Program.cs at all.
        foreach (Match m in token.Matches(
                     File.ReadAllText(Path.Combine(repoRoot, "AlRunner", "Infrastructure", "ExecFailure.cs"))))
            found.Add(m.Value);

        Assert.NotEmpty(found);
        var unclassified = found.Except(BundleFailureStage.KnownMarkers, StringComparer.Ordinal).ToList();
        Assert.True(unclassified.Count == 0,
            $"BundleFailureStage does not classify: {string.Join(", ", unclassified)}. "
            + "Add each to PreExecutionMarkers or ExecutionMarkers — an unclassified marker is "
            + "reported as a compile failure whatever it actually was.");
    }
}
