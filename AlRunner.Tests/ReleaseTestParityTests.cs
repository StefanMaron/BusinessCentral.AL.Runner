// ReleaseTestParityTests — makes "the release ran different tests than the pull request did"
// impossible to reach green again (issue #1976).
//
// What actually happened
// -----------------------
// .github/workflows/publish.yml carried its own hand-maintained copy of the BC test job.
// The copy was always a SUBSET of .github/workflows/test-matrix.yml's, and it kept being the
// wrong subset. Four divergences, each found only when a release broke:
//
//   * missing -p:AllowBcArtifactDownload=true   -> the build failed before a test ran
//   * missing the R2R platform-app download     -> 17 corpus tests failed on BC 27.3
//   * missing the generated .runsettings        -> 47 engine tests skipped; v2.3.0 failed
//   * missing the Cecil cache warm step         -> the same 47 skipped; v2.3.1 failed
//
// The last two are one defect found twice. The v2.3.0 fix copied two of the three steps that
// make the in-process engine tests run and left out the one that has to come FIRST: without a
// real runner invocation to warm ~/.cache/al-runner/ncl-cecil, the `cp` of
// Microsoft.Dynamics.Nav.Ncl.dll copies a still-pristine file, BcEngineBootstrap sees a cold
// rewrite, and every BcEngineCollection test skips — the exact condition
// BcEngineReadinessGuardTests exists to fail loudly on. It did fail loudly. Twice. The guard
// was never the problem; maintaining two copies of the job was.
//
// The fix is .github/workflows/bc-tests.yml: one reusable (workflow_call) definition that both
// test-matrix.yml and publish.yml call. This file is what stops the copy coming back — a
// re-inlined step in either caller fails here, in a normal unit-test run, instead of on the
// next release.
//
// Split the same way BcEngineReadinessGuardTests is, and for the same reason:
//   1. WorkflowParity.FindInlinedBcTestSteps is a pure function of the workflow text, proven
//      below against constructed strings — it does not need the real files to be provable.
//   2. The remaining tests wire that pure function to the REAL workflow files on disk.

using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Markers for steps that belong to the BC test matrix. They must appear in the shared
/// reusable workflow and NOWHERE ELSE — a caller that contains one has stopped delegating.
/// </summary>
internal static class WorkflowParity
{
    internal const string SharedWorkflow = "bc-tests.yml";

    /// <summary>The call a delegating workflow must contain.</summary>
    internal const string DelegationMarker = "uses: ./.github/workflows/bc-tests.yml";

    /// <summary>
    /// Each entry is (marker, what re-inlining it would silently cost). Chosen to be the
    /// load-bearing steps of the matrix, including the two whose absence actually failed a
    /// release, so this guard would have caught v2.3.0 and v2.3.1 before they were dispatched.
    /// </summary>
    // Every marker here is checked against the real bc-tests.yml by
    // SharedWorkflow_IsReusable_AndStillCarriesEveryBcTestStep, which is what keeps this list
    // honest: a marker that matches nothing would be a guard that guards nothing, and the
    // first draft of this list had two of those — "DownloadArtifacts -- platform-apps" never
    // appears anywhere, because the workflow wraps that command over a line continuation.
    internal static readonly (string Marker, string Cost)[] BcTestSteps =
    {
        ("dotnet test AlRunner.Tests", "the unit-test run itself"),
        ("DOTNET_STARTUP_HOOKS", "the .runsettings that make the 47 in-process engine tests run rather than skip"),
        ("ncl-cecil", "the Cecil cache warm step — without it the engine tests skip even WITH the .runsettings"),
        ("tools/DownloadArtifacts", "artifact provisioning: BC version resolution and the app downloads"),
        ("$HOME/.al-runner/platform-apps", "the R2R platform apps the corpus needs at runtime"),
        ("$HOME/.al-runner/test-apps", "the Microsoft test toolkit runner-extras depends on"),
        ("--count-baseline", "the guard against a bundle silently vanishing from the run"),
    };

    /// <summary>
    /// Returns the BC-test-step markers present in <paramref name="workflowText"/>, ignoring
    /// YAML comment lines so that a comment ABOUT a step is not mistaken for the step.
    /// Empty means the workflow delegates rather than restating the matrix.
    /// </summary>
    internal static IReadOnlyList<string> FindInlinedBcTestSteps(string workflowText)
    {
        var code = string.Join('\n', workflowText
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#')));

        return BcTestSteps
            .Where(s => code.Contains(s.Marker, StringComparison.Ordinal))
            .Select(s => s.Marker)
            .ToList();
    }

    private static readonly Regex JobHeader = new(@"^  ([A-Za-z0-9_-]+):\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Splits a workflow into its top-level jobs, keyed by job id. Only the region after the
    /// `jobs:` key is considered — `on:` and `env:` also have two-space keys, and
    /// `workflow_dispatch:` would otherwise read as a job.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> SplitJobs(string workflowText)
    {
        var lines = workflowText.Replace("\r\n", "\n").Split('\n');
        var jobs = new Dictionary<string, string>(StringComparer.Ordinal);

        var i = Array.FindIndex(lines, l => l.StartsWith("jobs:", StringComparison.Ordinal));
        if (i < 0) return jobs;

        string? current = null;
        var body = new StringBuilder();
        for (i++; i < lines.Length; i++)
        {
            var header = JobHeader.Match(lines[i]);
            if (header.Success)
            {
                if (current is not null) jobs[current] = body.ToString();
                current = header.Groups[1].Value;
                body.Clear();
                continue;
            }
            body.Append(lines[i]).Append('\n');
        }
        if (current is not null) jobs[current] = body.ToString();
        return jobs;
    }

    /// <summary>The job ids a job declares in <c>needs:</c>, in either the inline or list form.</summary>
    internal static IReadOnlyList<string> NeedsOf(string jobBody)
    {
        foreach (var line in jobBody.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || !trimmed.StartsWith("needs:", StringComparison.Ordinal)) continue;

            return trimmed["needs:".Length..].Trim().Trim('[', ']')
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.Trim('"', '\''))
                .ToList();
        }
        return Array.Empty<string>();
    }

    /// <summary>The ids of jobs whose body contains <paramref name="marker"/>, comments excluded.</summary>
    internal static IReadOnlyList<string> JobsContaining(string workflowText, string marker) =>
        SplitJobs(workflowText)
            .Where(j => string.Join('\n', j.Value.Split('\n').Where(l => !l.TrimStart().StartsWith('#')))
                              .Contains(marker, StringComparison.Ordinal))
            .Select(j => j.Key)
            .ToList();
}

public sealed class ReleaseTestParityTests
{
    private static readonly string WorkflowDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".github", "workflows"));

    private static string Read(string name)
    {
        var path = Path.Combine(WorkflowDir, name);
        Assert.True(File.Exists(path), $"expected workflow {name} at {path}");
        return File.ReadAllText(path);
    }

    // ---- the pure function, proven on constructed text ------------------------------

    [Fact]
    public void FindInlinedBcTestSteps_ReturnsEmpty_ForADelegatingWorkflow()
    {
        const string delegating = """
            jobs:
              test:
                needs: prepare
                uses: ./.github/workflows/bc-tests.yml
                with:
                  ref: v1.2.3
            """;

        Assert.Empty(WorkflowParity.FindInlinedBcTestSteps(delegating));
    }

    [Fact]
    public void FindInlinedBcTestSteps_NamesEveryReInlinedStep()
    {
        // The exact shape of publish.yml before #1976: a partial copy of the matrix, wrapped
        // over line continuations the way the real workflow writes it.
        const string reInlined = """
            jobs:
              test:
                steps:
                  - name: Download R2R platform apps
                    run: |
                      dotnet run --project tools/DownloadArtifacts -- \
                        platform-apps 28.1 "$HOME/.al-runner/platform-apps"
                  - name: Run unit tests
                    run: dotnet test AlRunner.Tests/AlRunner.Tests.csproj -c Release
            """;

        var found = WorkflowParity.FindInlinedBcTestSteps(reInlined);

        Assert.Equal(
            new[] { "dotnet test AlRunner.Tests", "tools/DownloadArtifacts", "$HOME/.al-runner/platform-apps" }
                .OrderBy(m => m, StringComparer.Ordinal),
            found.OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public void FindInlinedBcTestSteps_IgnoresCommentsThatMerelyMentionAStep()
    {
        // bc-tests.yml is referenced by name in the callers' explanatory comments; a comment
        // is documentation, not a re-inlined step, and must not trip the guard.
        const string commentedOnly = """
            jobs:
              test:
                # This used to run `dotnet test AlRunner.Tests` inline and drifted — see #1976.
                # It also lost the --count-baseline guard and the ncl-cecil warm step.
                uses: ./.github/workflows/bc-tests.yml
            """;

        Assert.Empty(WorkflowParity.FindInlinedBcTestSteps(commentedOnly));
    }

    // ---- the pure function wired to the real files on disk --------------------------

    [Theory]
    [InlineData("publish.yml")]
    [InlineData("test-matrix.yml")]
    public void Callers_DelegateToTheSharedWorkflow_AndInlineNoneOfItsSteps(string workflow)
    {
        var text = Read(workflow);

        Assert.Contains(WorkflowParity.DelegationMarker, text, StringComparison.Ordinal);

        var inlined = WorkflowParity.FindInlinedBcTestSteps(text);
        Assert.True(inlined.Count == 0,
            $"{workflow} re-inlines BC test steps instead of delegating to {WorkflowParity.SharedWorkflow}: "
            + string.Join(", ", inlined)
            + ". Two copies of this job drifted four times and failed two releases (#1976) — "
            + "add the step to bc-tests.yml so BOTH callers get it.");
    }

    [Fact]
    public void SharedWorkflow_IsReusable_AndStillCarriesEveryBcTestStep()
    {
        // The mirror image of the test above: delegation is worthless if the shared definition
        // quietly loses the steps. Deleting the Cecil warm step from bc-tests.yml would
        // otherwise leave both callers "delegating" to a job that skips 47 engine tests.
        var text = Read(WorkflowParity.SharedWorkflow);

        Assert.Contains("workflow_call:", text, StringComparison.Ordinal);

        var present = WorkflowParity.FindInlinedBcTestSteps(text);
        var missing = WorkflowParity.BcTestSteps
            .Where(s => !present.Contains(s.Marker))
            .Select(s => $"{s.Marker} ({s.Cost})")
            .ToList();

        Assert.True(missing.Count == 0,
            $"{WorkflowParity.SharedWorkflow} no longer runs: {string.Join("; ", missing)}");
    }

    [Fact]
    public void RequiredStatusCheck_StaysAJobInTestMatrixYml()
    {
        // "All BC versions passed" is the one required check in main's branch ruleset. A
        // check's context is the job name qualified by the CALLING job, so moving this job
        // into bc-tests.yml would rename it to "bc-tests / All BC versions passed" and leave
        // the ruleset requiring a context that never reports again — a permanently pending
        // check that blocks every pull request, or worse, one that is quietly dropped.
        var text = Read("test-matrix.yml");

        Assert.Contains("name: All BC versions passed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("All BC versions passed", Read(WorkflowParity.SharedWorkflow), StringComparison.Ordinal);
    }

    // ---- release ordering: tests gate every write (#1978) ---------------------------

    [Fact]
    public void SplitJobs_DoesNotMistakeWorkflowDispatchForAJob()
    {
        // `on:` and `env:` carry two-space keys too. Splitting from the top of the file
        // instead of from `jobs:` would report "workflow_dispatch" as a job and quietly
        // break every ordering assertion below.
        const string wf = """
            on:
              workflow_dispatch:
                inputs:
                  version:
                    required: true
            env:
              DOTNET_NOLOGO: true
            jobs:
              plan:
                runs-on: ubuntu-latest
              release:
                needs: [plan, test]
            """;

        Assert.Equal(new[] { "plan", "release" }, WorkflowParity.SplitJobs(wf).Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void NeedsOf_ReadsBothTheInlineAndTheSingleForm()
    {
        Assert.Equal(new[] { "plan", "test" }, WorkflowParity.NeedsOf("    needs: [plan, test]\n"));
        Assert.Equal(new[] { "plan" }, WorkflowParity.NeedsOf("    needs: plan\n"));
        Assert.Empty(WorkflowParity.NeedsOf("    runs-on: ubuntu-latest\n"));
    }

    [Fact]
    public void JobsContaining_FindsThePreOrderingShapeThatLeftDeadTags()
    {
        // publish.yml as it stood before #1978: the tag was pushed by a job that needed
        // nothing, so a red matrix still left the tag behind. v2.3.0 and v2.3.1 both.
        const string oldShape = """
            jobs:
              prepare:
                runs-on: ubuntu-latest
                steps:
                  - name: Generate CHANGELOG and push release commit + tag
                    run: |
                      git push origin main
                      git tag "$TAG"
                      git push origin "$TAG"
              test:
                needs: prepare
                uses: ./.github/workflows/bc-tests.yml
            """;

        var tagging = WorkflowParity.JobsContaining(oldShape, "git push origin \"$TAG\"");

        Assert.Equal(new[] { "prepare" }, tagging);
        Assert.Empty(WorkflowParity.NeedsOf(WorkflowParity.SplitJobs(oldShape)["prepare"]));
    }

    [Fact]
    public void EveryWriteInPublishYml_IsGatedOnTheTestMatrix()
    {
        // The property that matters: a red matrix must leave the repository untouched.
        // Before #1978 the tag went up first, and v2.3.0 and v2.3.1 are both tags on main
        // with no release behind them because of it.
        var text = Read("publish.yml");
        var jobs = WorkflowParity.SplitJobs(text);

        var testJob = jobs.Keys.Single(j => jobs[j].Contains(WorkflowParity.DelegationMarker, StringComparison.Ordinal));

        var writes = new (string Marker, string What)[]
        {
            ("git push origin \"$TAG\"", "the release tag"),
            // #2060: the push target used to be the literal "main" -- pushing to whatever
            // ref was actually dispatched instead is the fix, so the marker here has to be
            // the templated form or this assertion would start failing for the right reason
            // (the write moved) reported as the wrong one (no owner found at all).
            ("git push origin HEAD:${{ needs.plan.outputs.branch }}", "the CHANGELOG commit on the released branch"),
            ("dotnet nuget push", "the NuGet package"),
            ("softprops/action-gh-release", "the GitHub Release"),
        };

        foreach (var (marker, what) in writes)
        {
            var owners = WorkflowParity.JobsContaining(text, marker);
            Assert.True(owners.Count == 1,
                $"expected exactly one job to write {what}; found {owners.Count} ({string.Join(", ", owners)})");

            var needs = WorkflowParity.NeedsOf(jobs[owners[0]]);
            Assert.True(needs.Contains(testJob),
                $"publish.yml job '{owners[0]}' writes {what} without needing '{testJob}'. "
                + "A failed matrix would leave it behind — that is how v2.3.0 and v2.3.1 became "
                + "dead tags on main (#1978).");
        }
    }

    [Fact]
    public void PublishYml_TestsTheCommitItShips_NotWhateverMainBecomes()
    {
        // main can move during a 40-minute matrix run. The ref is pinned once, by the plan
        // job, and both the matrix and the release checkout read that same pinned value —
        // otherwise the tag could land on code this run never tested.
        var text = Read("publish.yml");
        var jobs = WorkflowParity.SplitJobs(text);

        var testJob = jobs.Keys.Single(j => jobs[j].Contains(WorkflowParity.DelegationMarker, StringComparison.Ordinal));
        var releaseJob = WorkflowParity.JobsContaining(text, "git push origin \"$TAG\"").Single();

        const string pinnedRef = "ref: ${{ needs.plan.outputs.ref }}";
        Assert.Contains(pinnedRef, jobs[testJob], StringComparison.Ordinal);
        Assert.Contains(pinnedRef, jobs[releaseJob], StringComparison.Ordinal);
    }
}
