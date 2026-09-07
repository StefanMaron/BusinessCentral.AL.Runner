// Issue #3409: .github/workflows/ms-surface.yml runs the WHOLE Microsoft BaseApp surface —
// 40,530 tests across 32 non-empty buckets — from one manual dispatch, in ONE job, and reports
// one combined total.
//
// A YAML workflow cannot be unit-tested the way the C# behind it can, so this file pins the
// properties that are load-bearing and cheap to lose in an edit. Two of them are the reason the
// file exists:
//
//   1. THE BUCKET LIST IS THE WHOLE SURFACE. Every non-empty bucket appears exactly once; no
//      entry names something the artifact does not ship; the three sources that are NOT run are
//      accounted for by name. A list that silently drops a bucket still produces a total, and
//      that total is wrong in a way nobody notices.
//
//   2. THE SURFACE WORKFLOW REUSES ms-bucket.yml rather than re-spelling it. bc-tests.yml's
//      provisioning drifted four times and failed two releases (#1976) because a copy existed;
//      ms-bucket.yml already owns provisioning, the backup cache, the pinned reader, the
//      company name, the emit timeout and the private cache root.
//
// THE INDEPENDENT REFERENCE, AND WHY IT IS DUPLICATED ON PURPOSE
// --------------------------------------------------------------
// ArtifactInventory below is the artifact's OWN listing, measured — not derived from the
// workflow this test is checking. Reading the reference out of the file under test is how a
// drift test comes to pass vacuously: "every bucket in the list is in the list" cannot fail. So
// the same fact is stated twice, independently, and the two must agree.
//
// Measured against BC 28.4.53241.54318 with the ranged central-directory read
// tools/DownloadArtifacts already does:
//
//     dotnet run --project tools/DownloadArtifacts -- test-sources 28.4.53241.54318 /tmp/x __NOSUCH__
//
// whose error path lists every Applications/BaseApp/Test/*.Source.zip the artifact ships. That
// is a fact about one BC build: if Microsoft adds a bucket, this constant is what has to be
// re-measured, and the assertions below are what make that a red test rather than a quietly
// smaller total. A run that names a bucket the artifact does NOT ship fails earlier still — the
// fetch step lists what it does ship and stops.
using Xunit;

namespace AlRunner.Tests;

internal static class WorkflowBlockScalar
{
    /// <summary>
    /// The lines of a <c>key: |</c> block scalar, trimmed, with comments and blanks dropped.
    /// Ends at the first non-blank line indented no deeper than the key. Empty when the key is
    /// absent — callers must assert a count, never just iterate.
    /// </summary>
    internal static IReadOnlyList<string> Of(string workflowText, string key)
    {
        var lines = workflowText.Replace("\r\n", "\n").Split('\n');
        var values = new List<string>();
        var keyIndent = -1;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var indent = line.Length - line.TrimStart().Length;
            if (keyIndent < 0)
            {
                var trimmed = line.TrimStart();
                if (trimmed == key + ": |" || trimmed == key + ": |-") keyIndent = indent;
                continue;
            }
            if (line.Trim().Length == 0) continue;
            if (indent <= keyIndent) break;
            var value = line.Trim();
            if (value.StartsWith('#')) continue;
            values.Add(value);
        }
        return values;
    }
}

public sealed class MsSurfaceWorkflowTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string GithubDir = Path.Combine(RepoRoot, ".github");

    private const string SurfaceWorkflow = "workflows/ms-surface.yml";
    private const string BucketWorkflow = "workflows/ms-bucket.yml";

    /// <summary>
    /// The 35 <c>*.Source.zip</c> entries under <c>Applications/BaseApp/Test/</c>, measured —
    /// 32 non-empty test buckets, 2 empty ones, and one shared library source.
    /// </summary>
    private static readonly string[] ArtifactInventory =
    {
        "Library-NoTransactions",
        "Tests-Bank", "Tests-CRM integration", "Tests-Cash Flow", "Tests-Cost Accounting",
        "Tests-Data Exchange", "Tests-Dimension", "Tests-ERM", "Tests-Fixed Asset",
        "Tests-General Journal", "Tests-Graph", "Tests-Integration", "Tests-Job",
        "Tests-Local", "Tests-Marketing", "Tests-Misc", "Tests-Monitor Sensitive Fields",
        "Tests-Permissions", "Tests-Physical Inventory", "Tests-Prepayment",
        "Tests-Rapid Start", "Tests-Report", "Tests-Resource", "Tests-Reverse",
        "Tests-SCM", "Tests-SCM-Assembly", "Tests-SCM-Manufacturing", "Tests-SCM-Service",
        "Tests-SINGLESERVER", "Tests-SMB", "Tests-TestLibraries", "Tests-Upgrade",
        "Tests-User", "Tests-VAT", "Tests-Workflow",
    };

    /// <summary>
    /// What the artifact ships and this workflow deliberately does NOT run. Tests-TestLibraries
    /// is 204 .al files with no <c>[Test]</c> method — a library the other buckets compile
    /// against; Tests-Local is empty in the W1 artifact; Library-NoTransactions is not a bucket
    /// at all. Running any of them would report a zero that reads like a failure.
    /// </summary>
    private static readonly string[] NotRun =
        { "Library-NoTransactions", "Tests-Local", "Tests-TestLibraries" };

    // The surface, measured on the 28.1.49838.53507 artifact. Constants, so a workflow whose
    // bucket list enumerates NOTHING fails loudly here instead of satisfying every "for each
    // bucket, …" assertion below by having nothing to iterate.
    private const int ExpectedTests = 40_530;
    private const int ExpectedBuckets = 32;

    private static string Read(string relative)
    {
        var path = Path.Combine(GithubDir, relative);
        Assert.True(File.Exists(path), $"expected {relative} at {path}");
        return File.ReadAllText(path);
    }

    private static string CodeOnly(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    private static IReadOnlyList<string> SurfaceBuckets() =>
        WorkflowBlockScalar.Of(CodeOnly(Read(SurfaceWorkflow)), "buckets");

    // ---- the block-scalar reader, proven on constructed text ---------------------------

    [Fact]
    public void BlockScalarOf_ReadsTheBlock_AndStopsAtTheNextKey()
    {
        const string wf = """
            jobs:
              surface:
                with:
                  buckets: |
                    Tests-ERM
                    Tests-Cash Flow

                    # a comment inside the block
                    Tests-SMB
                  test-data: true
            """;

        Assert.Equal(new[] { "Tests-ERM", "Tests-Cash Flow", "Tests-SMB" },
            WorkflowBlockScalar.Of(wf, "buckets"));
    }

    [Fact]
    public void BlockScalarOf_IsEmptyWhenTheKeyIsAbsentOrNotABlock()
    {
        Assert.Empty(WorkflowBlockScalar.Of("with:\n  test-data: true\n", "buckets"));
        Assert.Empty(WorkflowBlockScalar.Of("with:\n  buckets: Tests-ERM\n", "buckets"));
    }

    // ---- the bucket list is the whole surface ------------------------------------------

    /// <summary>
    /// The counts, asserted against constants BEFORE anything is compared element by element.
    /// A truncated or empty list fails here rather than passing the set comparisons below by
    /// having nothing in it.
    /// </summary>
    [Fact]
    public void SurfaceWorkflow_RunsEveryNonEmptyBucketExactlyOnce()
    {
        var buckets = SurfaceBuckets();

        Assert.Equal(ExpectedBuckets, buckets.Count);
        var duplicated = buckets.GroupBy(b => b, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(b => b).ToArray();
        Assert.Equal(Array.Empty<string>(), duplicated);
        Assert.Equal(ExpectedBuckets, buckets.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Both directions against the measured artifact listing: nothing invented, and nothing the
    /// artifact ships left silently out. The second half is the one that matters when Microsoft
    /// adds a bucket — without it, a 33-bucket artifact keeps producing a confident 32-bucket
    /// total.
    /// </summary>
    [Fact]
    public void SurfaceWorkflow_NamesOnlyRealBuckets_AndAccountsForEverythingTheArtifactShips()
    {
        var buckets = SurfaceBuckets();
        var inventory = ArtifactInventory.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Array.Empty<string>(),
            buckets.Where(b => !inventory.Contains(b)).OrderBy(b => b).ToArray());

        var accounted = buckets.Concat(NotRun).ToList();
        Assert.Equal(Array.Empty<string>(),
            ArtifactInventory.Where(s => !accounted.Contains(s, StringComparer.Ordinal))
                .OrderBy(s => s).ToArray());
        Assert.Equal(ArtifactInventory.Length, accounted.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The three sources that are not run are named in the workflow with the reason, so the
    /// operator reading "32 buckets" can see which two of the artifact's 34 Tests-* entries are
    /// missing and why — rather than wondering whether the list is just incomplete.
    /// </summary>
    [Fact]
    public void SurfaceWorkflow_SaysWhichSourcesItDoesNotRun()
    {
        var text = Read(SurfaceWorkflow);
        var buckets = SurfaceBuckets();

        foreach (var skipped in NotRun)
        {
            Assert.DoesNotContain(skipped, buckets);
            Assert.Contains(skipped, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 40,530 is not decoration: it is handed to ms-bucket.yml as <c>expected-tests</c>, and the
    /// combined-total step compares the measured total against it and says so when a large slice
    /// of the surface never ran. A bundle lost to COMPILE FAIL vanishes from the totals (#2715),
    /// so "the run finished and reported a number" is not the same as "the number covers the
    /// surface".
    /// </summary>
    [Fact]
    public void SurfaceWorkflow_DeclaresTheExpectedSurfaceSize_SoAShortfallIsVisible()
    {
        var code = CodeOnly(Read(SurfaceWorkflow));

        Assert.Contains($"expected-tests: {ExpectedTests}", code, StringComparison.Ordinal);
    }

    // ---- the surface workflow's shape --------------------------------------------------

    /// <summary>
    /// Manual dispatch ONLY. A multi-hour, 40,530-test job against an Actions queue shared
    /// account-wide with the corpus repository must never be started by a push, a pull request
    /// or a cron. <c>workflow_call</c> is forbidden too: this file is the POLICY (which buckets,
    /// how many), and anything wanting the mechanics calls ms-bucket.yml, which is what it is
    /// for.
    /// </summary>
    [Fact]
    public void SurfaceWorkflow_IsManualDispatchOnly()
    {
        Assert.Equal(new[] { "workflow_dispatch" }, WorkflowTriggers.TriggersOf(Read(SurfaceWorkflow)));
    }

    /// <summary>
    /// It calls ms-bucket.yml and therefore contains none of the configuration ms-bucket.yml
    /// owns. A second spelling of the company name, the package caches or the reader pin is how
    /// provisioning drifted four times (#1976) — and here it would silently produce a number
    /// measured under a different configuration from the nightly's, which is worse than no
    /// number.
    /// </summary>
    [Fact]
    public void SurfaceWorkflow_ReusesTheBucketWorkflow_RatherThanRespellingIt()
    {
        var code = CodeOnly(Read(SurfaceWorkflow));

        Assert.Contains("uses: ./.github/workflows/ms-bucket.yml", code, StringComparison.Ordinal);
        foreach (var owned in new[]
                 {
                     "--package-cache", "READER_TAG", "--test-data-company",
                     "CRONUS International Ltd_", "AL_RUNNER_EMIT_TIMEOUT_SEC",
                     "provision-bc", "al-runner",
                 })
            Assert.DoesNotContain(owned, code, StringComparison.Ordinal);
    }

    // ---- what the surface needs from ms-bucket.yml -------------------------------------

    /// <summary>
    /// The surface is several buckets in ONE job, so ms-bucket.yml grew a list input beside the
    /// singular one. The singular <c>bucket</c> stays — MsBucketWorkflowTests pins the rest of
    /// that file's shape and the nightly passes it.
    /// </summary>
    [Fact]
    public void BucketWorkflow_TakesABucketList_WithoutLosingTheSingularInput()
    {
        var code = CodeOnly(Read(BucketWorkflow));

        Assert.Contains("      bucket:", code, StringComparison.Ordinal);
        Assert.Contains("      buckets:", code, StringComparison.Ordinal);
        Assert.Contains("      label:", code, StringComparison.Ordinal);
        Assert.Contains("      expected-tests:", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The concurrency group must still vary when <c>bucket</c> is empty. It used to be
    /// <c>ms-bucket-${{ inputs.bucket }}</c>; a surface run passes only <c>buckets</c>, so
    /// without the label every list run would land in the group named <c>ms-bucket-</c> and
    /// queue behind any other list run — with <c>cancel-in-progress: false</c> that is a silent
    /// wait, not an error.
    /// </summary>
    [Fact]
    public void BucketWorkflow_ConcurrencyGroupVariesWhenOnlyAListIsGiven()
    {
        var code = CodeOnly(Read(BucketWorkflow));
        var group = code.Split('\n').First(l => l.TrimStart().StartsWith("group:", StringComparison.Ordinal));

        Assert.Contains("inputs.label", group, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runner is invoked once PER BUCKET, in separate processes. Peak RSS tracks how many
    /// bundles a process loads, not how many tests it runs — measured, 3 bundles / 939 tests
    /// peaked at 4.4 GB against 1 bundle / 1,027 tests at 3.7 GB — so handing 32 bundle
    /// directories to one invocation stacks their memory on a 16 GB runner that has already
    /// peaked at 10.9 GiB on Tests-ERM alone. Separate invocations reset it and still pay
    /// provisioning once.
    ///
    /// DOTNET_GCHeapCount is set explicitly rather than left to the runner's core count, and
    /// DOTNET_GCHeapHardLimit is forbidden: under ~1.25 GB it silently changed test results
    /// (259 -> 212 passing), so it would corrupt the very number this workflow exists to
    /// produce.
    /// </summary>
    [Fact]
    public void BucketWorkflow_RunsOneProcessPerBucket_WithAnExplicitHeapCount()
    {
        var code = CodeOnly(Read(BucketWorkflow));

        Assert.Contains("for bucket in \"${BUCKET_LIST[@]}\"", code, StringComparison.Ordinal);
        Assert.Contains("DOTNET_GCHeapCount:", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DOTNET_GCHeapHardLimit", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each bucket's numbers reach the job summary and the run's annotations BEFORE the next
    /// bucket starts, and the combined total comes after the loop. This is what makes a single
    /// job safe at a 360-minute ceiling that cannot be raised: a design whose failure mode is
    /// "the job was killed, so you get nothing" is the argument for sharding, and reporting as
    /// it goes removes it far more cheaply than a matrix does.
    ///
    /// Asserted as an ORDER, because that is the actual claim — a summary step placed after the
    /// loop would contain all the same strings and report nothing until the end.
    /// </summary>
    [Fact]
    public void BucketWorkflow_ReportsEachBucketBeforeTheNextOneStarts()
    {
        var code = CodeOnly(Read(BucketWorkflow));

        var loop = code.IndexOf("for bucket in \"${BUCKET_LIST[@]}\"", StringComparison.Ordinal);
        var perBucket = code.IndexOf("ms-bucket-summary.py", StringComparison.Ordinal);
        var combined = code.IndexOf("ms-bucket-total.py", StringComparison.Ordinal);

        Assert.True(loop >= 0, "the bucket loop is gone");
        Assert.True(perBucket > loop,
            "ms-bucket-summary.py must run INSIDE the loop, so a killed job still carries every "
            + "finished bucket's numbers");
        Assert.True(combined > perBucket, "the combined total comes after the per-bucket summaries");
        // The per-bucket append target, and the live annotation that survives a step the
        // runner never got to finalise — a step summary is written out when its step ENDS, and
        // here the whole run is one step.
        Assert.Contains("--step-summary \"$GITHUB_STEP_SUMMARY\"", code, StringComparison.Ordinal);
        Assert.Contains("--notice", code, StringComparison.Ordinal);
        Assert.Contains("::notice title=",
            File.ReadAllText(Path.Combine(RepoRoot, "scripts", "ms-bucket-summary.py")),
            StringComparison.Ordinal);

        // The combined total is the VERDICT, so it must run even after a bucket blew up — a
        // step that only runs on success would skip the one number the run exists to produce.
        Assert.Contains("- name: Combined total\n        if: always()", code, StringComparison.Ordinal);
    }
}
