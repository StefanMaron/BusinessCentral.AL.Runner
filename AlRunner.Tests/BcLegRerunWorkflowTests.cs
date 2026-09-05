// Issue #2649 follow-up: .github/workflows/bc-leg-rerun.yml re-runs ONE BC leg on demand,
// so `.claude/rules/ci-verdicts.md` section 5's flake-evidence standard ("a changing
// failing-leg set across two independent runs of the same code") can be satisfied without
// `gh run rerun`, which section 3 bans because it overwrites the failed run's log in place.
//
// The reason this needs a guard rather than a comment: `All BC versions passed` is the ONE
// required status check in main's branch ruleset (verified against the live ruleset — the
// other required context is `Tests updated`, and the individual `bc-tests / BC <ver>` legs
// are NOT required). If a SINGLE-leg run could report that context, one leg's green would
// satisfy a gate whose entire meaning is "all eight passed". That is a false green on the
// one check protecting main, and it is strictly worse than the flake it would be helping
// diagnose.
//
// The protection is structural, not conditional, and that distinction is the point:
// gating the aggregate job with an `if:` inside a dispatch-capable test-matrix.yml would
// make it report `skipped`, and GitHub has historically treated a skipped check run as
// SATISFYING a required check. So the aggregate job must not exist in the dispatch path at
// all — which is only true as long as the single-leg workflow stays a separate file that
// never declares it. That is what these tests hold in place.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcLegRerunWorkflowTests
{
    private static readonly string WorkflowDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".github", "workflows"));

    private const string Workflow = "bc-leg-rerun.yml";
    private const string RequiredCheckJobName = "All BC versions passed";

    private static string Read(string name)
    {
        var path = Path.Combine(WorkflowDir, name);
        Assert.True(File.Exists(path), $"expected workflow {name} at {path}");
        return File.ReadAllText(path);
    }

    private static string CodeOnly(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    [Fact]
    public void SingleLegRerun_IsManualDispatchOnly()
    {
        // A `push` or `pull_request` trigger here would run a SECOND matrix on every commit,
        // doubling the account-wide Actions queue this repo already contends for, and would
        // put a single-leg run on the same events the real gate uses.
        Assert.Equal(new[] { "workflow_dispatch" }, WorkflowTriggers.TriggersOf(Read(Workflow)));
    }

    [Fact]
    public void SingleLegRerun_NeverDeclaresTheRequiredAggregateCheck()
    {
        // The load-bearing assertion. Matches the job-name FORM (`name: <context>`) rather
        // than the bare string, so the file's own comments may explain the constraint
        // without tripping the guard that enforces it.
        var code = CodeOnly(Read(Workflow));

        Assert.DoesNotMatch(new Regex(@"name:\s*" + Regex.Escape(RequiredCheckJobName)), code);
    }

    [Fact]
    public void RequiredAggregateCheck_IsStillDeclaredExactlyOnce_InTestMatrixOnly()
    {
        // The mirror image: the guard above is worthless if the aggregate job stops existing
        // on the gating path, which would leave main's ruleset requiring a context that never
        // reports — a permanently pending check that blocks every pull request.
        Assert.Matches(new Regex(@"name:\s*" + Regex.Escape(RequiredCheckJobName)),
            CodeOnly(Read("test-matrix.yml")));
    }

    [Fact]
    public void SingleLegRerun_DelegatesToTheSharedMatrix_AndNarrowsItByVersion()
    {
        // Delegation, not a copy: a diagnostic leg has to run the REAL leg or its verdict
        // says nothing about the leg that failed (#1976 is the record of what a second
        // hand-maintained copy of these steps costs).
        var code = CodeOnly(Read(Workflow));

        Assert.Contains(WorkflowParity.DelegationMarker, code, StringComparison.Ordinal);
        Assert.Contains("bc-version-filter:", code, StringComparison.Ordinal);
        Assert.Empty(WorkflowParity.FindInlinedBcTestSteps(code));
    }

    [Fact]
    public void SingleLegRerun_UsesADistinctCallingJobName_SoItsLegContextsDoNotCollide()
    {
        // A check's context is the leg name qualified by the CALLING job. Naming this job
        // `bc-tests` would make a diagnostic leg report the identical context to the
        // pull-request path's (`bc-tests / BC <ver> (required)`) on the same head SHA.
        // Those legs are not required contexts, so that would not block a merge — but it
        // would make `gh pr checks` ambiguous about which run a verdict came from, which is
        // the stale-verdict trap ci-verdicts.md section 2 already exists for.
        var jobs = WorkflowParity.SplitJobs(CodeOnly(Read(Workflow)));

        Assert.DoesNotContain("bc-tests", jobs.Keys);
        Assert.Single(jobs);
    }

    [Fact]
    public void SingleLegRerun_DoesNotCancelInProgressRuns()
    {
        // Each run IS the measurement. `cancel-in-progress: true` would let a second
        // diagnostic dispatch destroy the data point the first one was collecting — the
        // same evidence-destruction this whole mechanism exists to avoid.
        var code = CodeOnly(Read(Workflow));

        Assert.Matches(new Regex(@"cancel-in-progress:\s*false"), code);
    }

    [Fact]
    public void SharedMatrix_DefaultsTheVersionFilterToEmpty_SoGatingCallersAreUnchanged()
    {
        // The filter narrows the matrix, so its default is what keeps every gating path
        // resolving all eight legs. A non-empty default would silently reduce main's gate
        // to one version.
        var shared = Read(WorkflowParity.SharedWorkflow);
        var filterBlock = Regex.Match(
            shared, @"bc-version-filter:.*?default:\s*''", RegexOptions.Singleline);

        Assert.True(filterBlock.Success,
            "bc-tests.yml must declare bc-version-filter with an empty default — a non-empty "
            + "default would narrow the required matrix for push, pull_request and release.");
    }

    [Fact]
    public void GatingCallers_NeverSetTheVersionFilter()
    {
        // test-matrix.yml gates main; publish.yml gates a release. Either one passing a
        // filter would mean the eight-leg promise is not what actually ran.
        foreach (var caller in new[] { "test-matrix.yml", "publish.yml" })
        {
            Assert.DoesNotContain("bc-version-filter", CodeOnly(Read(caller)), StringComparison.Ordinal);
        }
    }
}
