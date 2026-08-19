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
}
