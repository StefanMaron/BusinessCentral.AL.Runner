// CompileFailedBucketReportingTests — issue #2764.
//
// #2762/#2793 gave a bucket that RAN but LOST a suite a "Suite errors" section in the summary,
// because every number above it described only what survived. A bucket that failed to compile
// ENTIRELY has the same problem one step further along, and one channel fewer to solve it.
//
// Its tests are simply absent from the totals, so `Tests: / pass: / fail:` read exactly as a
// clean run of whatever else ran. `compile-fail:1` does move, but the line that says WHICH
// bundle and WHY is written when it happens — tens to thousands of lines above the block a
// developer actually reads — and `PrintPerTest`'s `=== X — COMPILE FAIL ===` sits up there
// with it.
//
// In a one-shot run the exit code still catches it. Under `--watch` there is no exit code at
// all: the summary IS the interface, nobody scrolls a live session, and the faster the loop
// the more it is trusted. That is why this one is worth fixing rather than filing.
//
// The fix is deliberately the SAME mechanism #2793 introduced rather than a second spelling of
// it: name the buckets in the summary and repeat their errors verbatim, printed only when
// there are any.
//
// This is entirely about the runner's own reporting — "what does al-runner print when a bundle
// does not compile" is not a question a service tier can adjudicate — so nothing here belongs
// in the al-language corpus (.claude/rules/bc-behavior-tests-go-upstream.md).
//
// The negatives carry as much weight as the positives: a section printed unconditionally, or
// one that folded compile-failed buckets into the test totals or into #2793's `partial:`
// counter, would satisfy "the failure is named" and still misstate the run.

using System;
using System.Collections.Generic;
using System.IO;
using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public sealed class CompileFailedBucketReportingTests
{
    private const string CompileError =
        "<bundled>: COMPILE-FAIL (8): error AL0118: The name 'Widgetz' does not exist.";

    private static TestResult Pass(string codeunit, string method) =>
        new(codeunit, method, TestOutcome.Pass, null, null, TimeSpan.FromMilliseconds(3));

    /// <summary>A bucket that did not compile at all: no tests, its errors on the record.</summary>
    private static BucketResult CompileFailedBucket(string path = "/bundle-broken") =>
        new(path, BucketStage.CompileFailed,
            new[] { CompileError }, null, Array.Empty<TestResult>(),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, null);

    /// <summary>A healthy sibling, so the run has something to report that looks clean.</summary>
    private static BucketResult CleanBucket(string path = "/bundle-ok") =>
        new(path, BucketStage.Ran,
            Array.Empty<string>(), null, new[] { Pass("Codeunit50100", "Healthy") },
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, null);

    private static string Summarise(params BucketResult[] buckets)
    {
        var w = new StringWriter();
        Reporter.PrintSummary(buckets, w);
        return w.ToString();
    }

    /// <summary>
    /// The claim: a cycle that lost a whole bundle must not print a summary a reader can mistake
    /// for a clean one. It has to name the bundle and say why, in the block being read.
    /// </summary>
    [Fact]
    public void CompileFailedBucket_IsNamedInTheSummary_WithItsErrorVerbatim()
    {
        var output = Summarise(CleanBucket(), CompileFailedBucket());

        Assert.Contains("/bundle-broken", output, StringComparison.Ordinal);
        // Verbatim, not paraphrased: this text is what names the surface and the diagnostic,
        // and a summary of it would send the reader back up the log.
        Assert.Contains(CompileError, output, StringComparison.Ordinal);
    }

    /// <summary>The Tests block — every line from "Tests:" to the timings.</summary>
    private static string TestsBlock(string summary)
    {
        var lines = summary.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        bool inBlock = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("Tests:", StringComparison.Ordinal)) inBlock = true;
            else if (inBlock && line.StartsWith("Time:", StringComparison.Ordinal)) break;
            if (inBlock) sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The heart of #2764, stated as the thing that makes the fix necessary rather than as a
    /// difference any change would satisfy: adding a bundle that did not compile leaves the
    /// TEST TOTALS byte-identical. Those are the numbers a developer glances at, so they cannot
    /// carry the signal and something else has to.
    /// </summary>
    [Fact]
    public void ACompileFailure_LeavesTheTestTotalsIdentical_SoTheNamingMustCarryIt()
    {
        var clean = Summarise(CleanBucket());
        var broken = Summarise(CleanBucket(), CompileFailedBucket());

        Assert.Equal(TestsBlock(clean), TestsBlock(broken));
        // ...which is exactly why the bundle has to be named somewhere the totals are not.
        Assert.DoesNotContain("/bundle-broken", clean, StringComparison.Ordinal);
        Assert.Contains("/bundle-broken", broken, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative: a clean run must be byte-identical to what it printed before. A section that
    /// appears unconditionally is noise, and every integration test asserting on the markers
    /// around it would be asserting past it.
    /// </summary>
    [Fact]
    public void CleanRun_PrintsNoCompileFailureSection()
    {
        var output = Summarise(CleanBucket());

        Assert.DoesNotContain("Compile failures", output, StringComparison.Ordinal);
        Assert.DoesNotContain("COMPILE-FAIL", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative: a compile-failed bucket is not a #2793 "partial" one. Folding it into that
    /// counter would claim the bundle ran and lost a suite, which is a different and milder
    /// thing than never having compiled.
    /// </summary>
    [Fact]
    public void CompileFailedBucket_IsNotCountedAsPartial()
    {
        var output = Summarise(CleanBucket(), CompileFailedBucket());

        Assert.Contains("  compile-fail:1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("partial:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Suite errors:", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Negative: its (absent) tests must not reach the totals. The bundle contributed nothing,
    /// and inventing a count for it would be a different lie than the one being fixed.
    /// </summary>
    [Fact]
    public void CompileFailedBucket_ContributesNoTestsToTheTotals()
    {
        var output = Summarise(CleanBucket(), CompileFailedBucket());

        Assert.Contains("Tests:         1 total", output, StringComparison.Ordinal);
        Assert.Contains("  pass:        1", output, StringComparison.Ordinal);
        Assert.Contains("  fail:        0", output, StringComparison.Ordinal);
    }
}
