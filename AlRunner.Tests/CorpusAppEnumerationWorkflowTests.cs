// Issue #2984: `.github/workflows/bc-tests.yml` named ONE corpus bundle path,
// `tests/al-language/tests/al-language`. The corpus then gained a second test app
// (`tests/al-language-onprem`, target OnPrem, for the `Scope = OnPrem` system tables a
// Cloud-target app cannot name at all — corpus PR #179). Because the workflow named one
// path, the submodule pin bump that pulls a new app in is green BY CONSTRUCTION: the app
// is checked out, never executed, and the leg still reports success.
//
// That is worse than the failure this repository already knows about, where a corpus leg
// goes green while visibly skipping codeunits: here nothing is skipped, because those
// tests never enter the run at all. Neither `--strict` (which fails on a test FAILING) nor
// `--count-baseline` (which compares the suites the run actually touched) can see a suite
// that was never handed to the runner — measured: with the fixture suite declared in the
// baseline, the single-app invocation still exits 0, because a declared suite the run did
// not touch is deliberately silent.
//
// So the workflow enumerates, and these tests hold that in place. They are the RED for
// #2984: every one of them fails against the pre-#2984 workflow.
//
// What is NOT asserted here, deliberately: which BC legs run, and what reports the two
// required contexts. `main`'s ruleset requires exactly `BC test matrix passed` and
// `Tests updated`; the per-leg `(required)` text is part of a job's own NAME and makes no
// leg a required context. AlRunner.Tests/BcLegRerunWorkflowTests.cs owns that property and
// nothing here may duplicate or weaken it.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class CorpusAppEnumerationWorkflowTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const string EnumerationScript = "scripts/corpus-app-dirs.py";

    /// <summary>The literal that must not come back as the corpus leg's bundle argument.</summary>
    private const string SingleAppPath = "tests/al-language/tests/al-language";

    private static string ReadWorkflow(string name)
    {
        var path = Path.Combine(RepoRoot, ".github", "workflows", name);
        Assert.True(File.Exists(path), $"expected workflow {name} at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The `run:` script of the gating corpus step, comments stripped. Located by the step's
    /// `- name:` line and ended at the next one, so the assertions below are about the step
    /// that actually runs the corpus rather than about the file as a whole — the xmlport
    /// order-independence step further down still names one app on purpose (it narrows to
    /// one codeunit with `--test`, so it is not a coverage gate).
    /// </summary>
    private static string GatingCorpusStepScript()
    {
        var lines = ReadWorkflow("bc-tests.yml").Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd() == "      - name: Run al-language corpus");
        Assert.True(start >= 0,
            "bc-tests.yml no longer has a step named 'Run al-language corpus'. If it was renamed, "
            + "update this guard rather than deleting it — it is what keeps a second corpus app "
            + "from being pulled in by the pin and never executed (#2984).");

        var end = Array.FindIndex(lines, start + 1, l => Regex.IsMatch(l, @"^      - name: "));
        if (end < 0) end = lines.Length;

        var body = string.Join('\n', lines[start..end]
            .Where(l => !l.TrimStart().StartsWith('#')));

        return body;
    }

    [Fact]
    public void GatingCorpusStep_DoesNotNameASingleHardcodedBundlePath()
    {
        // The defect, stated directly. Comments are stripped first so the step may keep
        // explaining the path it no longer passes.
        var body = GatingCorpusStepScript();

        Assert.DoesNotContain(SingleAppPath, body);
    }

    [Fact]
    public void GatingCorpusStep_EnumeratesTheCorpusAndPassesEveryAppAsItsOwnRoot()
    {
        var body = GatingCorpusStepScript();

        // Enumerated, not named.
        Assert.Contains(EnumerationScript, body);
        // Every enumerated app reaches the runner. `"${CORPUS_APPS[@]}"` — quoted, and the
        // array form: `$CORPUS_APPS` would pass only the first element, which is the exact
        // one-app-runs bug in a different disguise.
        Assert.Contains("\"${CORPUS_APPS[@]}\"", body);
        // The teeth. Enumeration without --strict runs the new app's tests and ignores their
        // failures; without a count guard a suite that stops being discovered is silent.
        Assert.Contains("--strict", body);
        // The count guard is no longer --count-baseline on this step (#3675): the corpus
        // suites are not declared in test-count-baseline.json, because the corpus is
        // resolved per run (#3737) and a committed exact count would go stale on every
        // upstream corpus merge. It is the compare step, asserted below.
        Assert.Contains("--out al-language-results.json", body);
    }

    /// <summary>
    /// The count guard, in the place it moved to (#3675). The corpus leg must still be
    /// unable to report green while running fewer tests than the last main run did — and
    /// the three steps below are what makes that true, so a leg keeping `--strict` and
    /// losing this is the same silent shrinkage in a new disguise.
    /// </summary>
    [Fact]
    public void CorpusLeg_ComparesItsTestCountAgainstMainsLastRecordedOne()
    {
        var wf = ReadWorkflow("bc-tests.yml");

        Assert.Contains(".github/scripts/compare_corpus_count.py", wf);
        // The comparison is only as good as the two ends it names: what this leg ran, and
        // the corpus it ran it against.
        Assert.Contains("--corpus-sha", wf);

        // ...and it must read the COUNT document, never the failure report. `--out` carries
        // `generated`/`total_failures`/`classifications`/`all_failures` and no test list at
        // all, so a comparison pointed at it refuses on every leg and the guard silently
        // never runs — which is exactly what shipped and what run 34412771210 caught.
        Assert.Contains("--count-out corpus-count-measured.json", wf);
        Assert.Contains("--counts corpus-count-measured.json", wf);
        Assert.DoesNotContain("--results al-language-results.json", wf);
        // Restored from the cache before the run, recorded after it.
        Assert.Contains("actions/cache/restore@v4", wf);
        Assert.Contains("actions/cache/save@v4", wf);

        // Only main records. Comparing a pull request against its own earlier count would
        // ratchet against itself: drop 50 tests, record 50 fewer, next push green.
        var save = wf[wf.IndexOf("- name: Record this count for the next run", StringComparison.Ordinal)..];
        var guard = save[..save.IndexOf("uses:", StringComparison.Ordinal)];
        Assert.Contains("github.ref == 'refs/heads/main'", guard);
        Assert.Contains("github.event_name != 'pull_request'", guard);
        // ...and a single-leg diagnostic dispatched against main with an explicit
        // corpus-ref must not write a corpus PULL REQUEST's count as main's.
        Assert.Contains("inputs.corpus-ref == ''", guard);

        // THE WEDGE (caught in review of #3737). `success()` here means a DROP is never
        // recorded, so main restores the same larger count on every later run — and so
        // does every pull request, through the shared prefix key — and fails against it
        // forever with no in-repo remedy. The save must be gated on whether a count was
        // MEASURED, which the compare step reports as an output, not on whether the step
        // passed. Exit 3 must still be excluded: the script returns before writing --out,
        // so the restored PREVIOUS document is what a bare always() would save under this
        // run's corpus SHA.
        Assert.DoesNotContain("success()", guard);
        Assert.Contains("steps.count.outputs.measured == 'true'", guard);

        // ...and `measured` alone is not enough, which is the trap the wedge fix opened.
        // It answers "could the compare script read a results document", NOT "did the
        // corpus run finish": Program.cs writes --out from the results it has whatever the
        // exit code, so a leg exiting 2 (a bundle could not execute) or 3 (a bundle could
        // not compile) still produces a SHORT results file. On main that would be: leg red
        // for the real failure, compare sees a large drop, measured=true, and the PARTIAL
        // count becomes main's baseline — after which a genuine suite disappearance inside
        // that margin passes, and the next full run reads the recovery as growth and bakes
        // the drop in. An upstream corpus commit the runner cannot compile yet is the
        // routine way a corpus move lands here (caught in round 2 of #3737's review).
        //
        // This cannot reintroduce the wedge: a legitimate corpus shrink has a GREEN corpus
        // step and a compare that exits 1, so it still records and still unwedges main.
        Assert.Contains("steps.al-language.outcome == 'success'", guard);

        var compare = wf[wf.IndexOf("- name: Compare the corpus count against main's last recorded one",
            StringComparison.Ordinal)..];
        compare = compare[..compare.IndexOf("- name: Record this count", StringComparison.Ordinal)];
        Assert.Contains("id: count", compare);
        // measured=true for exit 0 and exit 1, false otherwise — and the step still fails
        // on a drop, so the leg goes red while the number is recorded.
        Assert.Contains("measured=true", compare);
        Assert.Contains("measured=false", compare);
        Assert.Contains("exit \"$rc\"", compare);
    }

    /// <summary>
    /// Every job that RUNS the corpus must check one out, and say which one. There is no
    /// gitlink any more (#3737), so a job that forgot the step would run against an empty
    /// directory — and `--strict` over nothing is not red, it is a shorter run.
    /// </summary>
    [Fact]
    public void EveryCorpusJob_ChecksOutACorpusAndPrintsTheShaItResolved()
    {
        var wf = ReadWorkflow("bc-tests.yml");
        Assert.Contains("./.github/actions/checkout-corpus", wf);
        // The gating leg reads that step's output, which only exists if the step has an id.
        Assert.Contains("steps.corpus.outputs.sha", wf);

        var action = File.ReadAllText(Path.Combine(
            RepoRoot, ".github", "actions", "checkout-corpus", "action.yml"));
        // The one line that makes a verdict attributable to a corpus commit.
        Assert.Contains("corpus: $sha ($CORPUS_REF)", action);
        Assert.Contains("GITHUB_STEP_SUMMARY", action);
        // ...and it must never quietly substitute master for a ref it could not fetch.
        Assert.Contains("NOT falling back to master", action);
    }

    [Fact]
    public void GatingCorpusStep_FailsLoudlyWhenTheEnumerationYieldsNothing()
    {
        // An empty app list would put the leg straight back into "green because it ran
        // nothing" — the same shape as the bug, arrived at from the other side.
        var body = GatingCorpusStepScript();

        Assert.Contains("${#CORPUS_APPS[@]}", body);
        Assert.Matches(new Regex(@"::error::[^\n]*corpus-app-dirs"), body);
    }

    [Fact]
    public void GatingCorpusStep_NeverFeedsTheAppListThroughStdin()
    {
        // The corpus's own runner reads its test-app list with `while read ... <<< "$DIRS"`
        // and calls the `al` tool without redirecting stdin, so the child drains the
        // here-string and the loop runs exactly once however many apps are listed — silently,
        // leg still green (measured on corpus run 33996414648). Anything on this side that
        // pipes or here-strings the list into a loop containing the runner inherits that bug.
        var body = GatingCorpusStepScript();

        Assert.DoesNotContain("<<<", body);
        Assert.DoesNotMatch(new Regex(@"while\s+read"), body);
    }

    [Fact]
    public void EnumerationScript_IsCheckedInAndTested()
    {
        // A workflow calling a script that does not exist fails at run time on eight legs
        // instead of here in milliseconds.
        Assert.True(File.Exists(Path.Combine(RepoRoot, EnumerationScript)),
            $"{EnumerationScript} is referenced by bc-tests.yml but is not checked in.");

        // pr-gate.yml runs every scripts/tests/*.test.py; without this file the script's
        // own behaviour — including the loud empty-list failure above — is unasserted.
        Assert.True(File.Exists(Path.Combine(RepoRoot, "scripts", "tests", "corpus-app-dirs.test.py")),
            "scripts/tests/corpus-app-dirs.test.py is missing; the enumeration would be untested.");
    }

    /// <summary>
    /// This used to be <c>EveryCorpusAppHasACountBaselineEntry</c>: every corpus app had to
    /// carry a line in <c>test-count-baseline.json</c>, or its tests were unguarded.
    /// <para>The corpus suites left that file at #3675, because the corpus is resolved per
    /// run (#3737) and a committed exact count goes stale on every upstream merge. The
    /// property it protected did not go with it: a corpus app that arrives and is never
    /// executed must not be invisible. Two things hold it now — the enumeration above, which
    /// makes every app on disk a bundle root, and the per-run count comparison, which sees
    /// the total fall if an app stops being discovered.</para>
    /// <para>So what is asserted here is the half a static check can still make: the corpus
    /// suites are NOT declared in the baseline file. An entry for one would impose an exact
    /// count on a moving corpus and turn every leg red on the next upstream merge — the
    /// failure mode this arrangement exists to remove.</para>
    /// </summary>
    [Fact]
    public void CountBaseline_DeclaresNoCorpusSuite()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(
                RepoRoot, "tests", "expectations", "count-baseline", "test-count-baseline.json")));
        var declared = doc.RootElement.GetProperty("suites").EnumerateObject()
            .Select(p => p.Name).ToList();

        var corpusSuites = declared
            .Where(n => n.StartsWith("al-language", StringComparison.Ordinal))
            .ToList();

        Assert.True(corpusSuites.Count == 0,
            "test-count-baseline.json declares corpus suite(s) " + string.Join(", ", corpusSuites)
            + ". The corpus is resolved per run (#3737), so an exact committed count for it goes "
            + "stale the moment an upstream corpus PR merges — every BC leg red with nothing in "
            + "this repository to fix. The corpus count is compared in CI against the last count "
            + "a main run recorded (#3675); runner-extras keeps its committed baseline.");

        // ...and the file is not thereby empty: runner-extras still declares its groups, so
        // this test cannot pass by the file having lost everything.
        Assert.Contains("runner-extras", declared);
    }
}
