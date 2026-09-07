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

internal static class WorkflowStep
{
    /// <summary>
    /// One named step's text, from its <c>- name:</c> line to the next step's. Assertions
    /// about ORDER need this: <c>ms-bucket.yml</c> has two <c>for bucket in …</c> loops, so an
    /// index taken over the whole file answers a question about the FETCH loop while reading
    /// like a question about the run loop.
    /// </summary>
    internal static string BodyOf(string code, string stepName)
    {
        const string marker = "      - name: ";
        var start = code.IndexOf(marker + stepName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no step named \"{stepName}\" in the workflow");
        var next = code.IndexOf(marker, start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? code[start..] : code[start..next];
    }
}

internal static class ReusableCallInputs
{
    /// <summary>
    /// The <c>with:</c> mapping a caller hands to a reusable workflow: one entry per key
    /// declared directly under it, value as written. A <c>key: |</c> block scalar yields
    /// <c>"|"</c> — its lines sit deeper and are not keys. Empty when the job has no
    /// <c>with:</c>, so callers must assert a COUNT before iterating.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> Of(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n');
        var found = new List<(string, string)>();
        var withIndent = -1;
        var keyIndent = -1;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;
            var indent = line.Length - trimmed.Length;

            if (withIndent < 0)
            {
                if (trimmed == "with:") withIndent = indent;
                continue;
            }
            if (indent <= withIndent) { withIndent = trimmed == "with:" ? indent : -1; keyIndent = -1; continue; }
            if (keyIndent < 0) keyIndent = indent;
            if (indent != keyIndent) continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            found.Add((trimmed[..colon], trimmed[(colon + 1)..].Trim()));
        }
        return found;
    }
}

internal static class ReusableCallTypeCoercion
{
    /// <summary>
    /// The inputs a caller hands down as a bare <c>${{ … }}</c> expression while the called
    /// workflow declares them <c>type: number</c> — the shape that makes GitHub refuse to
    /// create the run at all (#3456). A literal is fine; <c>fromJSON(…)</c> is the coercion.
    /// </summary>
    internal static IReadOnlyList<string> UncoercedNumberInputs(string callerCode, string calleeText)
    {
        var bad = new List<string>();
        foreach (var (name, value) in ReusableCallInputs.Of(callerCode))
        {
            if (!value.Contains("${{", StringComparison.Ordinal)) continue;
            if (value.Contains("fromJSON(", StringComparison.Ordinal)) continue;
            if (!WorkflowInputDeclarations.Of(calleeText, name, "type:").Contains("number")) continue;
            bad.Add(name);
        }
        return bad;
    }
}

public sealed class MsSurfaceWorkflowTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string GithubDir = Path.Combine(RepoRoot, ".github");

    private const string SurfaceWorkflow = "workflows/ms-surface.yml";
    private const string BucketWorkflow = "workflows/ms-bucket.yml";
    private const string NightlyWorkflow = "workflows/ms-bucket-nightly.yml";

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

    /// <summary>See <see cref="WorkflowStep.BodyOf"/>; MsBucketWorkflowTests slices the same
    /// run step for the per-test timeout, so the slicer is shared rather than spelled twice.</summary>
    private static string StepBody(string code, string stepName) => WorkflowStep.BodyOf(code, stepName);

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
                     "provision-bc", "al-runner", "--test-timeout",
                     "--test-data-normalize-company",
                 })
            Assert.DoesNotContain(owned, code, StringComparison.Ordinal);
    }

    /// <summary>
    /// #3456: every dispatch of ms-surface.yml failed in three seconds with ZERO jobs, no log
    /// and no annotation, from the moment #3443 added <c>test-timeout: ${{ inputs.test-timeout }}</c>.
    ///
    /// A <c>workflow_dispatch</c> input reaches the <c>inputs</c> context as a STRING whatever
    /// its declared type says, and handing a string to a <c>workflow_call</c> input declared
    /// <c>type: number</c> makes GitHub refuse to create the run — before any job exists, so
    /// there is nothing to read afterwards. Booleans are NOT affected: <c>test-data</c> and
    /// <c>normalize-company</c> pass through bare and the run starts (measured, run
    /// 34166606192). So the rule is number-typed inputs only, and the coercion is
    /// <c>fromJSON()</c>.
    ///
    /// Checked over every caller of ms-bucket.yml rather than over the one input that broke:
    /// <c>expected-tests</c> is number-typed too and is a literal today, so the same edit is
    /// one passthrough away from being made again. What no test in this repository can cover
    /// is whether GitHub ACCEPTS the file — that is a property of GitHub's schema, not of the
    /// text, and only a dispatch answers it.
    /// </summary>
    [Fact]
    public void EveryCallerOfTheBucketWorkflow_CoercesTheNumberTypedInputsItHandsDown()
    {
        var callee = Read(BucketWorkflow);

        foreach (var caller in new[] { SurfaceWorkflow, NightlyWorkflow })
        {
            var handedDown = ReusableCallInputs.Of(CodeOnly(Read(caller)));
            Assert.NotEmpty(handedDown);

            var uncoerced = ReusableCallTypeCoercion.UncoercedNumberInputs(CodeOnly(Read(caller)), callee);
            Assert.True(uncoerced.Count == 0,
                $"{caller} hands ms-bucket.yml a bare ${{{{ … }}}} expression for the number-typed "
                + $"input(s) {string.Join(", ", uncoerced)}. GitHub refuses to create the run — zero "
                + "jobs, no log — so wrap the value in fromJSON() or pass a literal (#3456).");
        }

        // Non-vacuous in both directions: the machinery resolved a real number-typed input on
        // the callee side, and the surface really does hand that input down.
        Assert.Contains("number", WorkflowInputDeclarations.Of(callee, "test-timeout", "type:"));
        Assert.Contains(ReusableCallInputs.Of(CodeOnly(Read(SurfaceWorkflow))),
            i => i.Name == "test-timeout");
    }

    // ---- the with-block reader and the coercion rule, proven on constructed text ---------

    [Fact]
    public void ReusableCallInputs_ReadsOnlyTheWithBlocksOwnKeys()
    {
        const string code = """
            jobs:
              surface:
                uses: ./.github/workflows/ms-bucket.yml
                with:
                  label: surface
                  expected-tests: 40530
                  test-timeout: ${{ inputs.test-timeout }}
                  buckets: |
                    Tests-ERM
                    Tests-SCM
            """;

        var inputs = ReusableCallInputs.Of(code);

        Assert.Equal(new[] { "label", "expected-tests", "test-timeout", "buckets" },
            inputs.Select(i => i.Name));
        Assert.Equal("40530", inputs.Single(i => i.Name == "expected-tests").Value);
        Assert.Equal("${{ inputs.test-timeout }}", inputs.Single(i => i.Name == "test-timeout").Value);
        // A block scalar's lines are deeper than the keys and are not keys themselves.
        Assert.Equal("|", inputs.Single(i => i.Name == "buckets").Value);
        Assert.DoesNotContain(inputs, i => i.Name == "uses");
        Assert.Empty(ReusableCallInputs.Of("jobs:\n  surface:\n    uses: ./x.yml\n"));
    }

    [Fact]
    public void UncoercedNumberInputs_NamesTheBareExpressionAndNothingElse()
    {
        const string callee = """
            on:
              workflow_call:
                inputs:
                  test-timeout:
                    type: number
                    default: 300
                  expected-tests:
                    type: number
                    default: 0
                  test-data:
                    type: boolean
                    default: true
                  bc-version:
                    type: string
                    default: ''
            """;

        const string broken = """
            jobs:
              surface:
                with:
                  test-timeout: ${{ inputs.test-timeout }}
                  expected-tests: 40530
                  test-data: ${{ inputs.test-data }}
                  bc-version: ${{ inputs.bc-version }}
            """;

        // The number-typed passthrough is named; the number LITERAL, the boolean passthrough
        // and the string passthrough are not — booleans and strings coerce, numbers do not.
        Assert.Equal(new[] { "test-timeout" },
            ReusableCallTypeCoercion.UncoercedNumberInputs(broken, callee));

        var fixedUp = broken.Replace("${{ inputs.test-timeout }}", "${{ fromJSON(inputs.test-timeout) }}");
        Assert.Empty(ReusableCallTypeCoercion.UncoercedNumberInputs(fixedUp, callee));
    }

    /// <summary>
    /// #3431: the surface ran with the runner's 60 s wall-clock default per-test watchdog on a
    /// hosted runner, so tests that finish comfortably inside it locally were cut off.
    ///
    /// The surface owns the POLICY knob and ms-bucket.yml owns putting it on the command line.
    /// So two things: the value is PASSED THROUGH rather than re-spelled — a hardcoded number
    /// here would silently stop tracking an override — and the two files' defaults are equal,
    /// so a dispatch that leaves the field untouched measures under the same watchdog as the
    /// nightly, which passes nothing at all. Without that equality the three callers drift and
    /// their numbers stop being comparable, which is the one property this measurement needs.
    /// </summary>
    [Fact]
    public void SurfaceWorkflow_PassesThePerTestTimeoutThrough_AtTheSameDefault()
    {
        Assert.Contains("test-timeout: ${{ fromJSON(inputs.test-timeout) }}",
            CodeOnly(Read(SurfaceWorkflow)), StringComparison.Ordinal);

        var surface = WorkflowInputDefaults.Of(Read(SurfaceWorkflow), "test-timeout");
        var bucket = WorkflowInputDefaults.Of(Read(BucketWorkflow), "test-timeout");

        Assert.Single(surface);
        Assert.NotEmpty(bucket);
        Assert.Equal(bucket.Distinct(StringComparer.Ordinal).Single(), surface[0]);
    }

    /// <summary>
    /// #3450: the surface is where the prepared-company comparison gets run, so it needs the
    /// knob — passed THROUGH, at the same default, for the same two reasons the timeout is.
    ///
    /// The default is asserted to be <c>false</c> by name here rather than only "equal to
    /// ms-bucket.yml's". Equality alone would survive both files being flipped to <c>true</c>
    /// together, and that is the change that silently invalidates every number this repository
    /// has recorded: the skill's 259/595 for Tests-SMB, #3416's corpus counts, and the
    /// full-surface runs were all measured against the un-normalized restore. Turning it on is
    /// a dispatch-time decision, never a default.
    /// </summary>
    [Fact]
    public void SurfaceWorkflow_PassesCompanyNormalizationThrough_AtTheSameDefault_Off()
    {
        Assert.Contains("normalize-company: ${{ inputs.normalize-company }}",
            CodeOnly(Read(SurfaceWorkflow)), StringComparison.Ordinal);

        var surface = WorkflowInputDefaults.Of(Read(SurfaceWorkflow), "normalize-company");
        var bucket = WorkflowInputDefaults.Of(Read(BucketWorkflow), "normalize-company");

        Assert.Equal("false", Assert.Single(surface));
        Assert.NotEmpty(bucket);
        Assert.Equal("false", bucket.Distinct(StringComparer.Ordinal).Single());
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
    /// bucket starts. This is the property the whole single-job design rests on: a hosted job
    /// cannot run past 360 minutes and that ceiling cannot be raised, so "the job was killed,
    /// so you get nothing" must not be the failure mode. Reporting as it goes removes it far
    /// more cheaply than sharding does.
    ///
    /// The assertion is that the reporting call sits INSIDE the loop — between the `for` and
    /// its `done` — not merely somewhere after the loop began. An earlier version of this test
    /// compared indices against the loop's START, and a reviewer showed it passing on the exact
    /// regression it claims to catch: reporting moved into a second loop after `done`, which
    /// reports everything at the end and nothing during the run. `done` is also after the
    /// loop's start, so that arrangement satisfied the old comparison with 24 green tests.
    ///
    /// Two loops named `for bucket in "${BUCKET_LIST[@]}"` exist in the file — the fetch step
    /// has one too, and it comes FIRST — so this reads indices inside the run step's own body
    /// rather than over the whole file. The old version did not, which made it weaker still
    /// than the paragraph above admits: it was satisfied by anything after the FETCH loop.
    /// </summary>
    [Fact]
    public void BucketWorkflow_ReportsEachBucketBeforeTheNextOneStarts()
    {
        var code = CodeOnly(Read(BucketWorkflow));
        var runStep = StepBody(code, "Run the bucket(s)");

        var loop = runStep.IndexOf("for bucket in \"${BUCKET_LIST[@]}\"", StringComparison.Ordinal);
        Assert.True(loop >= 0, "the bucket loop is gone from the run step");
        var done = runStep.IndexOf("\n          done", loop, StringComparison.Ordinal);
        Assert.True(done > loop, "the bucket loop in the run step never closes");

        var perBucket = runStep.IndexOf("ms-bucket-summary.py", StringComparison.Ordinal);
        Assert.True(perBucket > loop && perBucket < done,
            "ms-bucket-summary.py must be called INSIDE the bucket loop, between `for` and "
            + "`done`. After `done` it reports every bucket only once the whole run has "
            + "finished, so a job killed at the 360-minute ceiling carries away every number "
            + "it had already measured — the failure mode this design exists to avoid.");

        // The combined total is the opposite: once, after the loop, in its own step.
        var combined = code.IndexOf("ms-bucket-total.py", StringComparison.Ordinal);
        Assert.True(combined > code.IndexOf(runStep, StringComparison.Ordinal) + runStep.Length,
            "the combined total belongs after the run step, not inside it");
        Assert.DoesNotContain("ms-bucket-total.py", runStep, StringComparison.Ordinal);

        // The per-bucket append target, and the live annotation that survives a step the
        // runner never got to finalise — a step summary is written out when its step ENDS, and
        // here the whole run is one step.
        Assert.Contains("--step-summary \"$GITHUB_STEP_SUMMARY\"", runStep, StringComparison.Ordinal);
        Assert.Contains("--notice", runStep, StringComparison.Ordinal);
        Assert.Contains("::notice title=",
            File.ReadAllText(Path.Combine(RepoRoot, "scripts", "ms-bucket-summary.py")),
            StringComparison.Ordinal);

        // The combined total is the VERDICT, so it must run even after a bucket blew up — a
        // step that only runs on success would skip the one number the run exists to produce.
        Assert.Contains("- name: Combined total\n        if: always()", code, StringComparison.Ordinal);
    }
}
