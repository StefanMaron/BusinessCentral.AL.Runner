// ProvisionGapSummaryTests — issues #2587 and #4560.
//
// Two messages predict a run's failure exactly: DependencyResolver's unservable dependencies,
// and DependencyLoader's symbol-only platform-app note. #2587 repeated them in the run summary,
// where a caller reading the bottom of a long log finds them. #4560 moved them there OUTRIGHT:
// printing each at discovery too repeated it once per dependency edge (17 blocks for 7 apps on
// one test app), so the closing "Action needed" block is now the only default-verbosity copy,
// one entry per app.
//
// This is entirely about the runner's own reporting — there is no claim about Business Central
// anywhere in this file, so nothing here belongs in the al-language corpus.
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

    private static string ActionNeeded(params BucketResult[] buckets)
    {
        var w = new StringWriter();
        Reporter.PrintActionNeeded(buckets, w);
        return w.ToString();
    }

    private static int Occurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // The shape DependencyResolver reports: one `[dep] Publisher/Name vX` block per dependency
    // EDGE, so two dependents of one symbols-only app hand the collector the same app twice —
    // with a different winner path when the two edges resolved different copies.
    private const string AssertViaEdgeA =
        "[dep] Microsoft/Library Assert v28.1.49838.55128 resolved to a package with NO IMPLEMENTATION (no publishedartifacts DLL,"
        + "\n      no src/*.al) and no other copy was found in the package caches:"
        + "\n      winner: /cache-a/Microsoft_Library Assert.app";
    private const string AssertViaEdgeB =
        "[dep] Microsoft/Library Assert v28.1.49838.55128 resolved to a package with NO IMPLEMENTATION (no publishedartifacts DLL,"
        + "\n      no src/*.al) and no other copy was found in the package caches:"
        + "\n      winner: /cache-b/Microsoft_Library Assert.app";
    private const string AnyGap =
        "[dep] Microsoft/Any v28.1.49838.55128 resolved to a package with NO IMPLEMENTATION (no publishedartifacts DLL,"
        + "\n      no src/*.al) and no other copy was found in the package caches:"
        + "\n      winner: /cache-a/Microsoft_Any.app";

    /// <summary>
    /// #4560's proving test: two dependents of one symbols-only app print ONE entry — keyed on
    /// the line naming the app, not on the whole message (the winner path differs per edge).
    /// </summary>
    [Fact]
    public void TwoDependentsOfOneSymbolsOnlyApp_PrintOneEntry()
    {
        var output = ActionNeeded(Bucket("/bundle-a", new[] { AssertViaEdgeA, AnyGap, AssertViaEdgeB }));

        Assert.Contains("Action needed (2):", output);
        Assert.Equal(1, Occurrences(output, "Microsoft/Library Assert v28.1.49838.55128"));
        Assert.Equal(1, Occurrences(output, "Microsoft/Any v28.1.49838.55128"));
        // Verbatim, including the continuation lines: the entry names the winner path.
        Assert.Contains("winner: /cache-a/Microsoft_Library Assert.app", output);
    }

    /// <summary>
    /// One dependent app missing two different floors is two things to fix: the key is the
    /// whole first line, not the app it starts with.
    /// </summary>
    [Fact]
    public void TwoFloorsOfOneDependent_AreTwoEntries()
    {
        const string floorA = "[dep] Contoso/Widgets v1.0.0.0 declares a floor of Microsoft/System >= 28.1.0.0, and it cannot be supplied:"
            + "\n      no copy was found in any searched directory";
        const string floorB = "[dep] Contoso/Widgets v1.0.0.0 declares a floor of Microsoft/Business Foundation >= 28.1.0.0, and it cannot be supplied:"
            + "\n      no copy was found in any searched directory";

        var output = ActionNeeded(Bucket("/bundle-a", new[] { floorA, floorB }));

        Assert.Contains("Action needed (2):", output);
    }

    /// <summary>Two different VERSIONS of one app are two things to fix, not one.</summary>
    [Fact]
    public void TheSameAppAtTwoVersions_IsTwoEntries()
    {
        var older = AssertViaEdgeA.Replace("v28.1.49838.55128", "v27.0.38460.53934");

        var output = ActionNeeded(Bucket("/bundle-a", new[] { AssertViaEdgeA, older }));

        Assert.Contains("Action needed (2):", output);
    }

    [Fact]
    public void EveryGapIsNamedVerbatim_AndCounted()
    {
        const string gapA = "[deps] Microsoft_Base Application v28.1 is symbol-only — run al-runner provision";
        const string gapB = "[deps] Contoso_Widgets v1.0 has neither a DLL nor AL source — nothing can serve it";

        var output = ActionNeeded(Bucket("/bundle-a", new[] { gapA, gapB }));

        // Distinct first lines are distinct entries, whatever shape the message has.
        Assert.Contains(gapA, output);
        Assert.Contains(gapB, output);
        Assert.Contains("Action needed (2):", output);
    }

    [Fact]
    public void NoGaps_NoBlockAtAll()
    {
        // Printed unconditionally, the block would be noise on every run.
        Assert.Equal("", ActionNeeded(Bucket("/bundle-a", null)));
    }

    [Fact]
    public void TheSameGapReportedByTwoBundles_IsOneEntry()
    {
        const string gap = "[deps] Microsoft_Base Application v28.1 is symbol-only";

        var output = ActionNeeded(Bucket("/bundle-a", new[] { gap }), Bucket("/bundle-b", new[] { gap }));

        Assert.Contains("Action needed (1):", output);
    }

    [Fact]
    public void AGapInOneBundleOnly_IsStillReported()
    {
        // The per-bundle carrier must not lose a gap because a LATER bundle was clean.
        const string gap = "[deps] Contoso_Widgets v1.0 has neither a DLL nor AL source";

        var output = ActionNeeded(Bucket("/bundle-a", new[] { gap }), Bucket("/bundle-b", null));

        Assert.Contains(gap, output);
        Assert.Contains("Action needed (1):", output);
    }

    /// <summary>
    /// The summary no longer carries the gaps: they print once, in the Action needed block.
    /// A summary that still printed them would put every gap on screen twice.
    /// </summary>
    [Fact]
    public void TheSummaryDoesNotRepeatTheGaps()
    {
        var w = new StringWriter();
        Reporter.PrintSummary(new[] { Bucket("/bundle-a", new[] { AssertViaEdgeA }) }, w);

        Assert.DoesNotContain("Library Assert", w.ToString());
        Assert.DoesNotContain("Provisioning gaps", w.ToString());
    }
    // ── #4636: a run that aborts in the bundle loop never reaches the closing block ──

    private static string OnAbort(bool deferred, IReadOnlyList<BucketResult> finished, params string[] pending)
    {
        var w = new StringWriter();
        Reporter.PrintActionNeededOnAbort(finished, pending, deferred, w);
        return w.ToString();
    }

    /// <summary>
    /// An abort prints what was deferred: an earlier bundle's gap AND the aborting bundle's own,
    /// one entry per app, in the same block the closing path would have printed.
    /// </summary>
    [Fact]
    public void OnAbort_Deferred_PrintsEarlierBundlesGapsAndThisBundlesOwn()
    {
        var output = OnAbort(true, new[] { Bucket("/bundle-a", new[] { AnyGap }) }, AssertViaEdgeA, AssertViaEdgeB);

        Assert.Contains("Action needed (2):", output);
        Assert.Equal(1, Occurrences(output, "Microsoft/Any v28.1.49838.55128"));
        Assert.Equal(1, Occurrences(output, "Microsoft/Library Assert v28.1.49838.55128"));
    }

    /// <summary>Negative: gaps already written at discovery (--verbose, --server, --output-json) are not repeated.</summary>
    [Fact]
    public void OnAbort_NotDeferred_PrintsNothing()
    {
        Assert.Equal("", OnAbort(false, new[] { Bucket("/bundle-a", new[] { AnyGap }) }, AssertViaEdgeA));
    }

    [Fact]
    public void OnAbort_Deferred_NoGaps_PrintsNothing()
    {
        Assert.Equal("", OnAbort(true, Array.Empty<BucketResult>()));
    }

    /// <summary>
    /// Wiring: every `return 1;` between the bundle's gap list and its BucketResult is an abort
    /// that skips the closing block, so each must print the pending gaps first. Zero sites found
    /// means the anchors moved, not that the loop is clean.
    /// </summary>
    [Fact]
    public void EveryEarlyReturnInTheBundleLoop_PrintsThePendingGapsFirst()
    {
        var programCs = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner", "Program.cs"));
        var lines = File.ReadAllLines(programCs);
        int start = Array.FindIndex(lines, l => l.Contains("var bundleProvisionGaps = new List<string>();"));
        int end = start < 0 ? -1 : Array.FindIndex(lines, start, l => l.Contains("results.Add(new BucketResult(bundleAbs"));
        Assert.True(start >= 0 && end > start, $"bundle-loop anchors not found in {programCs} (start={start}, end={end})");

        var unguarded = new List<int>();
        int sites = 0;
        for (int i = start + 1; i < end; i++)
        {
            if (lines[i].Trim() != "return 1;") continue;
            sites++;
            if (lines[i - 1].Trim() != "Reporter.PrintActionNeededOnAbort(results, bundleProvisionGaps);")
                unguarded.Add(i + 1);
        }
        Assert.True(sites > 0, "no `return 1;` found in the bundle loop: the scan measured nothing");
        Assert.True(unguarded.Count == 0,
            $"early return(s) at Program.cs line(s) {string.Join(", ", unguarded)} drop deferred provisioning gaps (#4636)");
    }
}
