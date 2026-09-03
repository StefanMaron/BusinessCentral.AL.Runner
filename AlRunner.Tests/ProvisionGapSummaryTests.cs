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

// Separate class: ProvisionGapLog is process-global state, so these must not interleave with
// anything else touching it. They are pure in-memory calls, so serialising them costs nothing.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class ProvisionGapLogTests
{
    [Fact]
    public void Report_WritesToStderrAndRecords()
    {
        var original = Console.Error;
        var captured = new StringWriter();
        try
        {
            ProvisionGapLog.Reset();
            Console.SetError(captured);
            ProvisionGapLog.Report("a gap");
        }
        finally { Console.SetError(original); }

        // Loud FIRST, recorded SECOND. .claude/rules/loud-failures.md means the summary is an
        // addition; nothing about this may get quieter, so the stderr half is asserted too.
        Assert.Contains("a gap", captured.ToString());
        Assert.Equal(new[] { "a gap" }, ProvisionGapLog.Collected);
    }

    [Fact]
    public void Reset_ForgetsThePreviousBundlesGaps()
    {
        var original = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            ProvisionGapLog.Reset();
            ProvisionGapLog.Report("bundle one's missing package");
            Assert.Single(ProvisionGapLog.Collected);

            // Without this, bundle one's gap is attributed to every later bundle and every later
            // --watch cycle — the run would keep reporting a problem the current bundle does not
            // have.
            ProvisionGapLog.Reset();
            Assert.Empty(ProvisionGapLog.Collected);
        }
        finally { Console.SetError(original); }
    }

    [Fact]
    public void Report_KeepsDiscoveryOrder()
    {
        var original = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            ProvisionGapLog.Reset();

            // Deliberately NOT in alphabetical order. Written the obvious way — "first",
            // "second", "third" — the three strings sort into the order they were added, so the
            // assertion passes against an implementation that sorts and proves only that three
            // things came back. Measured: with sorted inputs, replacing the getter with
            // OrderBy(...) still passed.
            ProvisionGapLog.Report("zeta app resolved symbol-only");
            ProvisionGapLog.Report("alpha app has neither a DLL nor AL source");
            ProvisionGapLog.Report("mid app resolved symbol-only");

            // The fourth property of this collector, alongside "records", "reset clears" and
            // "Collected is a copy" below. It is observable end to end: PrintSummary dedupes with
            // Enumerable.Distinct, which keeps first-occurrence order, so the summary lists gaps
            // in the same order as the stderr blocks thousands of lines above it — which is what
            // lets a reader match the two up. Every other assertion in this class uses a single
            // entry, so nothing else here would notice a reordering.
            Assert.Equal(
                new[]
                {
                    "zeta app resolved symbol-only",
                    "alpha app has neither a DLL nor AL source",
                    "mid app resolved symbol-only",
                },
                ProvisionGapLog.Collected);
        }
        finally { Console.SetError(original); }
    }

    [Fact]
    public void Collected_IsACopy_SoALaterResetDoesNotEmptyWhatACallerAlreadyRead()
    {
        var original = Console.Error;
        try
        {
            Console.SetError(TextWriter.Null);
            ProvisionGapLog.Reset();
            ProvisionGapLog.Report("a gap");
            var read = ProvisionGapLog.Collected;

            ProvisionGapLog.Reset();

            // Program.cs reads Collected into the bundle's own list and the next bundle resets.
            // Handing out the live list would empty that bundle's record from under it.
            Assert.Equal(new[] { "a gap" }, read);
        }
        finally { Console.SetError(original); }
    }
}
