// ProvisionGapSummaryTests — issue #2587.
//
// Two messages predict a run's failure exactly and were printed only at the point of discovery:
// DependencyResolver's unservable dependencies, and DependencyLoader's symbol-only platform-app
// note. On a long run they scroll thousands of lines above the summary the caller reads, so a
// caller reading the bottom concludes their AL is broken when their package cache is
// unprovisioned.
//
// This is entirely about the runner's own reporting — there is no claim about Business Central
// anywhere in this file, so nothing here belongs in the al-language corpus.
//
// The negative tests are what make the positive one mean something. A summary section that
// printed unconditionally, or a collector that never reset, would satisfy "the gap is named" and
// still be wrong.
using System;
using System.Collections.Generic;
using System.IO;
using AlRunner;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ProvisionGapSummaryTests
{
    private static BucketResult Bucket(string path, IReadOnlyList<string>? gaps) =>
        new(path, BucketStage.Ran,
            Array.Empty<string>(), null, Array.Empty<TestResult>(),
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, gaps);

    private static string Summarize(params BucketResult[] buckets)
    {
        var w = new StringWriter();
        Reporter.PrintSummary(buckets, w);
        return w.ToString();
    }

    [Fact]
    public void Summary_NamesEveryGapVerbatim_AndCountsThem()
    {
        const string gapA = "[deps] Microsoft_Base Application v28.1 is symbol-only — run al-runner provision";
        const string gapB = "[deps] Contoso_Widgets v1.0 has neither a DLL nor AL source — nothing can serve it";

        var summary = Summarize(Bucket("/bundle-a", new[] { gapA, gapB }));

        // Verbatim, because each block is what names the app, the winning path and the fix
        // command. A summary that paraphrased them would send the reader back up the log.
        Assert.Contains(gapA, summary);
        Assert.Contains(gapB, summary);
        Assert.Contains("Provisioning gaps: 2", summary);
    }

    [Fact]
    public void Summary_WithNoGaps_HasNoSectionAtAll()
    {
        // The negative that keeps a clean run's output unchanged. Printed unconditionally, this
        // section would be noise on every run and would sit between the existing markers and the
        // closing rule, where the integration tests assert.
        var summary = Summarize(Bucket("/bundle-a", null));

        Assert.DoesNotContain("Provisioning gaps", summary);
    }

    [Fact]
    public void Summary_DeduplicatesTheSameGapReportedByTwoBundles()
    {
        // Two bundles in one run resolving the same missing platform app report it twice. The
        // reader needs to know it is one problem, not two.
        const string gap = "[deps] Microsoft_Base Application v28.1 is symbol-only";

        var summary = Summarize(
            Bucket("/bundle-a", new[] { gap }),
            Bucket("/bundle-b", new[] { gap }));

        Assert.Contains("Provisioning gaps: 1", summary);
    }

    [Fact]
    public void Summary_GapInOneBundleOnly_IsStillReported()
    {
        // The per-bundle carrier must not lose a gap because a LATER bundle was clean.
        const string gap = "[deps] Contoso_Widgets v1.0 has neither a DLL nor AL source";

        var summary = Summarize(
            Bucket("/bundle-a", new[] { gap }),
            Bucket("/bundle-b", null));

        Assert.Contains(gap, summary);
        Assert.Contains("Provisioning gaps: 1", summary);
    }
}
