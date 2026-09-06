// Issue #3003: `main`'s post-merge matrix is cancelled far more often than it completes, so
// `main`'s health is not red — it is UNKNOWN, and unknown is worse than red because nothing
// signals it.
//
// Measured 2026-09-06 over the 100 most recent Test Matrix runs on `main` (18.5 h):
//
//   conclusion            cancelled 83 | success 13 | failure 3 | in progress 1
//   merge interval        median 359 s, mean 673 s  -> ~130 pushes/day
//   full matrix run       mean 22.8 min wall, max 26.4 min, 74.6 job-minutes over 12 jobs
//
// A 23-minute run against a 6-minute median merge interval essentially never survives. The
// mechanism is `cancel-in-progress: true` on a concurrency group keyed by `github.ref`, which
// on `main` is the constant `refs/heads/main`, so every merge cancels the run the previous
// merge started.
//
// WHAT A CANCELLED RUN COSTS, AND WHY IT IS LESS THAN IT LOOKS
//
// Job-level timings over a 20-run sample of the 83 cancelled runs: mean 27.7 job-minutes,
// median 14.3, against 74.6 for a full run — so a cancelled run costs 37% of a full one and
// cancelling saves about 63%. The matrix legs are not CREATED until `resolve-versions`
// finishes, so a run cancelled early burns almost nothing; five of the twenty had zero jobs
// ever started, and across all 83 walls 49% died under six minutes. (The low job queue delay
// — median 3 s over 179 jobs in the busiest hour — does not bear on this: it measures
// job-creation-to-job-start, not run-creation-to-leg-start.)
//
// So test-matrix.yml's existing header was roughly right about the cost it was addressing.
// This workflow does not overturn that decision; it covers what that decision leaves
// uncovered.
//
// WHY THE FIX IS NOT "STOP CANCELLING"
//
// Giving each push its own group avoids any queue, but the tail is unaffordable: replaying
// the observed arrival times against a 22.8-minute run gives a peak of 11 concurrent `main`
// runs — 72 concurrent jobs from `main` alone, on an Actions concurrency pool scoped per
// ACCOUNT and shared with this repo's pull-request CI and the corpus repository's.
//
// `cancel-in-progress: false` on the existing shared group is the simplest alternative and is
// NOT ruled out — it is unmeasured. rho = lambda * W = (130/day) * 22.8 min = 2.05, but that
// implies an unbounded queue only if same-group runs queue without bound, which nothing here
// establishes. `main` ran in that configuration until c93598f8 (2026-09-03T21:31Z): over the
// 37.3 h before, 84 runs at 70% completion with wall times inflated by queueing (max 77.9 min
// against 26.4 today) — queueing, but not unbounded, and at 2.25 runs/h against today's 5.40.
// The workflow header names the ten-minute experiment that would settle it.
//
// WHAT THIS WORKFLOW DOES INSTEAD
//
// A floor: `main` may not go longer than one cadence without a conclusive verdict. The
// push-triggered matrix keeps cancelling — that is correct and stays — and a scheduled run
// re-establishes the verdict on `main` HEAD whenever cancellation destroyed it.
//
// Time-weighted mean concurrent jobs attributable to `main`, over the same 18.5 h window:
//
//   today (cancelling)    3.14 jobs
//   never cancel `main`   6.72 jobs   (+3.57)   PEAK 72
//   scheduled floor       +2.09 jobs            PEAK 12   (bounded by design)
//
// The floor is about 1.7x cheaper than never cancelling as well as 6x smaller on the tail.
// Its peak is bounded by construction: one run at a time (its own concurrency group with
// cancellation off) and a cadence longer than the longest observed run, so a floor run can
// never overlap its own successor.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class MainVerdictFloorWorkflowTests
{
    private static readonly string WorkflowDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".github", "workflows"));

    private const string Floor = "main-verdict-floor.yml";
    private const string Gating = "test-matrix.yml";
    private const string RequiredCheckJobName = "All BC versions passed";

    /// <summary>
    /// Longest `main` Test Matrix run observed in the 100-run sample above, in minutes. The
    /// floor's cadence must exceed it, or a floor run can be overtaken by its own successor —
    /// which with cancellation off means queueing, the unstable-queue failure mode this
    /// workflow exists to avoid.
    /// </summary>
    private const int LongestObservedRunMinutes = 27;

    private static string Read(string name)
    {
        var path = Path.Combine(WorkflowDir, name);
        Assert.True(File.Exists(path), $"expected workflow {name} at {path}");
        return File.ReadAllText(path);
    }

    private static string CodeOnly(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    [Fact]
    public void Floor_RunsOnlyOnAScheduleAndOnDemand()
    {
        // The cost guard. A `push` trigger here would run a SECOND eight-leg matrix on every
        // merge — 130 extra runs a day — and a `pull_request` trigger would put a
        // non-gating reporter on the same events the real gate uses.
        Assert.Equal(new[] { "schedule", "workflow_dispatch" }, WorkflowTriggers.TriggersOf(Read(Floor)));
    }

    [Fact]
    public void Floor_CadenceExceedsTheLongestObservedMatrixRun()
    {
        // Derived, not chosen: a cadence shorter than a run's wall time makes runs overlap,
        // and with `cancel-in-progress: false` overlap means a queue with rho > 1. The
        // measured mean is 22.8 min and the measured max 26.4 min, so the cadence floor is
        // 27 min; `*/30` is the first cron-expressible value above it.
        var cron = Regex.Match(CodeOnly(Read(Floor)), @"cron:\s*'\*/(\d+) \* \* \* \*'");

        Assert.True(cron.Success,
            "the floor must declare a fixed-minute cadence (`cron: '*/N * * * *'`) so this "
            + "guard can compare N against the longest observed run.");
        Assert.True(int.Parse(cron.Groups[1].Value) > LongestObservedRunMinutes,
            $"cadence is {cron.Groups[1].Value} min but the longest observed `main` matrix run "
            + $"is {LongestObservedRunMinutes} min — floor runs would overlap and queue.");
    }

    [Fact]
    public void Floor_NeverCancelsItself_AndDoesNotShareTheGatingGroup()
    {
        // A cancelled floor run reproduces the bug it exists to fix. Sharing the gating
        // workflow's group would be worse still: a merge would cancel the floor, so the
        // floor could never outlive the merge rate that defeats the push-triggered run.
        var code = CodeOnly(Read(Floor));

        Assert.Matches(new Regex(@"cancel-in-progress:\s*false"), code);
        Assert.DoesNotMatch(new Regex(@"group:.*github\.ref"), code);
    }

    [Fact]
    public void Floor_NeverDeclaresTheRequiredAggregateCheck()
    {
        // `All BC versions passed` is one of main's two required status checks. A second
        // workflow reporting that context on the same head SHA makes `gh pr checks`
        // ambiguous about which run a verdict came from — the stale-verdict trap in
        // ci-verdicts.md section 2. Matches the job-name FORM so the file's comments may
        // name the context while explaining why they must not declare it.
        Assert.DoesNotMatch(
            new Regex(@"name:\s*" + Regex.Escape(RequiredCheckJobName)), CodeOnly(Read(Floor)));
    }

    [Fact]
    public void Floor_DelegatesToTheSharedMatrix_AtFullWidth()
    {
        // Delegation rather than a copy: a hand-maintained second spelling of these steps
        // drifted four times and failed two releases (#1976). And no version filter — a
        // floor that ran three of eight legs would re-introduce exactly the BC-version
        // blindness it is meant to remove.
        var code = CodeOnly(Read(Floor));

        Assert.Contains(WorkflowParity.DelegationMarker, code, StringComparison.Ordinal);
        Assert.Empty(WorkflowParity.FindInlinedBcTestSteps(code));
        Assert.DoesNotContain("bc-version-filter", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Floor_SkipsWhenMainHeadAlreadyHasAConclusiveVerdict()
    {
        // The floor is a floor, not a second matrix. When the push-triggered run survived —
        // 16% of the time in the sample, and most of the time overnight — HEAD's verdict is
        // already known and running it again buys nothing. The guard keys on the head SHA
        // because that is what a verdict is about (ci-verdicts.md section 2).
        var code = CodeOnly(Read(Floor));
        var jobs = WorkflowParity.SplitJobs(code);

        Assert.Contains("verdict-needed", jobs.Keys);
        Assert.Contains("head_sha=", jobs["verdict-needed"], StringComparison.Ordinal);

        // and the expensive job must actually be gated on it, or the guard is decoration
        var matrix = jobs.Single(j => j.Value.Contains(WorkflowParity.DelegationMarker, StringComparison.Ordinal));
        Assert.Contains("needs: verdict-needed", matrix.Value, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"if:\s*needs\.verdict-needed\.outputs\.\w+\s*==\s*'true'"), matrix.Value);
    }

    [Fact]
    public void Floor_UsesADistinctCallingJobName_SoItsLegContextsDoNotCollide()
    {
        // A check's context is the leg name qualified by the CALLING job, so a job id of
        // `bc-tests` here would make the floor's legs report the identical context to the
        // gating path's (`bc-tests / BC <ver> (required)`) on the same head SHA — and unlike
        // bc-leg-rerun.yml, the floor runs on `main`, which is exactly where the gating path
        // also reports. `gh pr checks` could then not tell which run a verdict came from.
        var code = CodeOnly(Read(Floor));
        var jobs = WorkflowParity.SplitJobs(code);

        Assert.DoesNotContain("bc-tests", jobs.Keys);

        // The id is only half of it. A `name:` overrides the id in the context, so keeping the
        // id `floor-matrix` while adding `name: bc-tests` would collide exactly as before and
        // pass an id-only check. There is no `name:` on the calling job today, which is what
        // makes the id load-bearing — so assert both, or the guard has a hole the size of the
        // thing it guards.
        Assert.DoesNotMatch(new Regex(@"name:\s*[""']?bc-tests[""']?\s*$", RegexOptions.Multiline), code);
    }

    [Fact]
    public void OnlyTheGatingMatrixAndTheFloor_RunTheEightLegMatrixOnPushesToMain()
    {
        // The census behind "the same shape does not repeat elsewhere". Two workflows trigger
        // on a push to `main`: this repo's gating matrix, and sync-changelog-unreleased.yml.
        // The changelog sync shares the pattern — one concurrency group, cancellation on —
        // but not the pathology: it takes a median of 11 s against a 359 s median merge
        // interval (rho = 0.03), so 56 of its last 60 runs completed. Cancellation starves a
        // workflow only when its wall time approaches the merge interval, which is a property
        // of the run, not of the concurrency block.
        //
        // This guard fails if a third push-to-main workflow starts calling the eight-leg
        // matrix, because that would double `main`'s post-merge cost without anyone pricing
        // it — the thing #3003 is about.
        var callers = Directory.GetFiles(WorkflowDir, "*.yml")
            .Where(f => CodeOnly(File.ReadAllText(f))
                .Contains(WorkflowParity.DelegationMarker, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // publish.yml gates a release and bc-leg-rerun.yml is dispatch-only; neither runs on
        // a push. ms-bucket.yml is not here — it drives the provisioning action directly
        // rather than the shared matrix.
        Assert.Equal(
            new[] { "bc-leg-rerun.yml", Floor, "publish.yml", Gating },
            callers);
    }

    [Fact]
    public void GatingMatrix_StillCancelsSupersededRuns()
    {
        // The mirror, and the reason this change is safe. Cancellation on pull requests is
        // correct and wanted: a superseded commit's verdict is worthless and its slots are
        // taken from every other repository on the account. Turning it off here — on a group
        // keyed by `github.ref` — would queue rather than parallelise, at rho = 2.05. This
        // guard fails if a future change trades main's silent gap for a saturated queue.
        var code = CodeOnly(Read(Gating));

        Assert.Matches(new Regex(@"group:.*github\.ref"), code);
        Assert.Matches(new Regex(@"cancel-in-progress:\s*true"), code);
    }
}
