// ExecuteFailedBucketReportingTests — issue #2831, the sibling of #2764/#2829.
//
// #2829 gave a bucket that failed to COMPILE a named section in the summary, because its tests
// contribute nothing and every number in the Tests block therefore reads as a clean run of
// whatever else ran. A bucket that built and loaded and then failed to RUN has the identical
// shape: `BucketStage.ExecuteFailed` is counted in `exec-fail:` and skipped by the same
// `continue`, so it contributes no tests either.
//
// REPRODUCTION (this one is real, not hypothetical — it is what #2779's own incident was):
//
//     al-runner <bundle> --test-data=<a file that is not a SQL Server backup>
//
//     EfTd: EXEC-FAIL: the backup reader failed (exit 1): error: no MQDA data stream found …
//       compile-fail:0
//       exec-fail:   1
//     Tests:         0 total
//
// Every AL object compiled cleanly. `Tests: 0 total / pass: 0 / fail: 0` is what a run with
// nothing to do prints too.
//
// WHY ITS OWN HEADING, NOT #2829's
//
// They are different failures with different next actions, and #2779 exists precisely because
// conflating them sends the reader to the wrong place: a bundle whose backup reader refused the
// backup was reported as `compile-fail: 1`, and "the reader of that report goes looking for AL
// compile errors that do not exist". Reusing the "Compile failures" heading here would
// reintroduce that mis-direction one layer up, in the block a developer actually reads. A
// compile failure says look at your AL; an execution failure says the module was fine and
// something around it died.
//
// Entirely about the runner's own reporting — no claim about Business Central — so nothing here
// belongs in the al-language corpus (.claude/rules/bc-behavior-tests-go-upstream.md).

using System;
using System.IO;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class ExecuteFailedBucketReportingTests
{
    private const string ExecError =
        "EfTd: EXEC-FAIL: the backup reader failed (exit 1): error: no MQDA data stream found "
        + "— not a SQL Server full database backup";

    private static TestResult Pass(string codeunit, string method) =>
        new(codeunit, method, TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(3));

    /// <summary>A bucket that built and loaded, then failed to run. No tests; errors on record.</summary>
    private static BucketResult ExecuteFailedBucket(string path = "/bundle-exec-dead") =>
        new(path, BucketStage.ExecuteFailed,
            new[] { ExecError }, null, Array.Empty<TestResult>(),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, null);

    /// <summary>The same, with the reason in ProcessError instead — the fan-out path's shape.</summary>
    private static BucketResult ExecuteFailedBucketWithProcessError(string path = "/bundle-worker-died") =>
        new(path, BucketStage.ExecuteFailed,
            Array.Empty<string>(), "worker process exited with code 139", Array.Empty<TestResult>(),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, null);

    private static BucketResult CleanBucket(string path = "/bundle-ok") =>
        new(path, BucketStage.Ran,
            Array.Empty<string>(), null, new[] { Pass("Codeunit50100", "Healthy") },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

    private static BucketResult CompileFailedBucket(string path = "/bundle-broken") =>
        new(path, BucketStage.CompileFailed,
            new[] { "<bundled>: COMPILE-FAIL (1): error AL0118: no such name." }, null,
            Array.Empty<TestResult>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, null);

    private static string Summarise(params BucketResult[] buckets)
    {
        var w = new StringWriter();
        Reporter.PrintSummary(buckets, w);
        return w.ToString();
    }

    [Fact]
    public void ExecuteFailedBucket_IsNamedInTheSummary_WithItsErrorVerbatim()
    {
        var output = Summarise(CleanBucket(), ExecuteFailedBucket());

        Assert.Contains("/bundle-exec-dead", output, StringComparison.Ordinal);
        Assert.Contains(ExecError, output, StringComparison.Ordinal);
    }

    /// <summary>The fan-out shape carries its reason in ProcessError, not CompileErrors.</summary>
    [Fact]
    public void ExecuteFailedBucket_ProcessError_IsReportedToo()
    {
        var output = Summarise(CleanBucket(), ExecuteFailedBucketWithProcessError());

        Assert.Contains("/bundle-worker-died", output, StringComparison.Ordinal);
        Assert.Contains("worker process exited with code 139", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The decision recorded as a test: an execution failure must NOT be filed under the
    /// compile heading. #2779 exists because that conflation sent readers looking for AL
    /// compile errors that did not exist; doing it here would reintroduce that one layer up.
    /// </summary>
    [Fact]
    public void ExecutionFailures_AreNotFiledUnderTheCompileHeading()
    {
        var output = Summarise(CleanBucket(), ExecuteFailedBucket());

        Assert.Contains("Execution failures:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Compile failures:", output, StringComparison.Ordinal);
    }

    /// <summary>Both kinds in one run: two headings, each naming only its own bucket.</summary>
    [Fact]
    public void BothKinds_AreReportedSeparately()
    {
        var output = Summarise(CleanBucket(), CompileFailedBucket(), ExecuteFailedBucket());

        Assert.Contains("Compile failures:", output, StringComparison.Ordinal);
        Assert.Contains("Execution failures:", output, StringComparison.Ordinal);
        var compileIdx = output.IndexOf("Compile failures:", StringComparison.Ordinal);
        var execIdx = output.IndexOf("Execution failures:", StringComparison.Ordinal);
        var brokenIdx = output.IndexOf("/bundle-broken", StringComparison.Ordinal);
        var deadIdx = output.IndexOf("/bundle-exec-dead", StringComparison.Ordinal);
        // Each bucket sits under its own heading, not merely somewhere in the output.
        Assert.InRange(brokenIdx, compileIdx, execIdx);
        Assert.True(deadIdx > execIdx, "the exec-failed bucket must sit under the execution heading");
    }

    /// <summary>Negative: nothing printed when there is nothing to say.</summary>
    [Fact]
    public void CleanRun_PrintsNoExecutionFailureSection()
    {
        var output = Summarise(CleanBucket());

        Assert.DoesNotContain("Execution failures", output, StringComparison.Ordinal);
        Assert.DoesNotContain("EXEC-FAIL", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative: an execute-failed bucket is not a #2793 "partial" one, and its absent tests
    /// must not reach the totals. Inventing a count would be a different lie than the one
    /// being fixed.
    /// </summary>
    [Fact]
    public void ExecuteFailedBucket_IsNotPartial_AndContributesNoTests()
    {
        var output = Summarise(CleanBucket(), ExecuteFailedBucket());

        Assert.Contains("  exec-fail:   1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("partial:", output, StringComparison.Ordinal);
        Assert.Contains("Tests:         1 total", output, StringComparison.Ordinal);
        Assert.Contains("  pass:        1", output, StringComparison.Ordinal);
    }
}
