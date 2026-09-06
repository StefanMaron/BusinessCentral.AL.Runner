// Issue #3141: the pull-request BC matrix runs 3 legs, not 8, and the required status check
// is named for what it actually checked.
//
// The measurement behind the narrowing is in the issue: over 37 days, 28.1, 28.2, 28.3 and
// 27.3 never once reported a failure that a neighbouring leg on the same commit did not,
// while 27.0 and 27.5 each caught defects no 28.x leg caught. So the 27/28 split is real and
// the middle minors are paying for a result the run already has. `main` still gets all eight,
// on a schedule and on every push, so a regression only 28.2 can see is caught within the
// schedule interval rather than never.
//
// Why this needs a guard rather than a comment. Three separate silent-failure shapes live in
// this change, and every one of them reports green:
//
//   1. A narrowed matrix under a check still called "All BC versions passed" asserts that all
//      BC versions passed when three of them did not run. The name is what a person reads
//      when deciding to trust a green PR, so the rename is part of the change, and the name
//      has to stay identical in FOUR places at once — the job in test-matrix.yml, the branch
//      ruleset, DEFAULT_REQUIRED_CONTEXTS and RULESET_CONTEXTS. #2785 is the record of what
//      those last two drifting apart costs.
//   2. A typo in the pull-request version list resolves to an empty or wrong matrix. An empty
//      matrix means the `test` job never runs, and a workflow whose only real job never ran
//      still reports success (#1976, #2065).
//   3. `versions-json` narrowing along with the matrix. The `pack` job builds one engine
//      variant per entry in that output and then asserts the packed count equals the number
//      of entries in bc-versions.txt — so a narrowed versions-json turns every pull request's
//      pack job red, and "fixing" that by relaxing the assertion would ship a nupkg missing
//      five engine variants (#2024, #2166).
//
// Deliberately NOT here: dropping the second AlRunner.Tests run. The issue proposed running
// the C# suite once, on 28.4, on the grounds that the 27.5 copy has never disagreed with it.
// Measured against 288 failed Test Matrix runs between 2026-08-02 and 2026-09-06, that is
// false: run 33337113550 (2026-08-30) failed
// ProvisionExplicitModesTests.TestApps_BundleDeclaresOlderMajor_StillTargetsEngineMajor on
// 27.0, 27.3 AND 27.5 with all four 28.x legs green — a clean major-version split, and the
// fix comment still sitting in that test names the cause (a hardcoded "27" that collides with
// the engine's own major only on a 27.x leg). The duplicate earns its cost, so
// PullRequestList_KeepsEveryLegThatRunsTheCSharpSuite holds it in place.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class PullRequestMatrixScopeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string GithubDir = Path.Combine(RepoRoot, ".github");

    private const string FullVersionsFile = "bc-versions.txt";
    private const string PrVersionsFile = "pr-bc-versions.txt";

    /// <summary>
    /// The one required status check `main`'s ruleset gates on, besides "Tests updated".
    /// Spelled once here; the tests below prove every other copy of it agrees.
    /// </summary>
    private const string RequiredContext = "BC test matrix passed";

    /// <summary>The name this replaces — it over-claimed the moment the matrix narrowed.</summary>
    private const string RetiredContext = "All BC versions passed";

    private static string Read(string workflowFile) =>
        File.ReadAllText(Path.Combine(GithubDir, "workflows", workflowFile)).Replace("\r\n", "\n");

    private static string ReadRepo(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot, relative)).Replace("\r\n", "\n");

    private static string CodeOnly(string text) =>
        string.Join('\n', text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    private static string[] Prefixes(string file)
    {
        var path = Path.Combine(GithubDir, file);
        Assert.True(File.Exists(path), $"expected {file} at {path}");
        return File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith('#'))
            .SelectMany(l => l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
    }

    private static string NewestOf(IEnumerable<string> prefixes) =>
        prefixes.OrderBy(p => Version.Parse(p)).Last();

    private static string ResolveStep()
    {
        // The `Resolve BC versions` run block — the shell that decides which legs exist.
        var text = Read("bc-tests.yml");
        var start = text.IndexOf("- name: Resolve BC versions", StringComparison.Ordinal);
        Assert.True(start > 0, "bc-tests.yml must still carry a 'Resolve BC versions' step");
        var end = text.IndexOf("\n  test:", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of the resolve-versions job");
        return text[start..end];
    }

    // ---- the pull-request version list ------------------------------------------------

    [Fact]
    public void PullRequestList_IsAProperSubsetOfTheFullList()
    {
        var all = Prefixes(FullVersionsFile);
        var pr = Prefixes(PrVersionsFile);

        Assert.NotEmpty(pr);
        // A prefix here that bc-versions.txt does not carry resolves to a leg the release
        // path never tests, and `pack`'s per-variant assertion would not cover it either.
        Assert.Empty(pr.Except(all, StringComparer.Ordinal));
        // Equal lists would make this file pure overhead pretending to be a decision.
        Assert.True(pr.Length < all.Length,
            $"{PrVersionsFile} ({pr.Length}) must be strictly smaller than {FullVersionsFile} ({all.Length})");
        Assert.Equal(pr.Length, pr.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PullRequestList_CoversEveryMajorTheFullListCarries()
    {
        // The whole finding is that the 27-vs-28 boundary discriminates and the minors inside
        // each major do not. Dropping a major entirely would throw away the one axis that
        // has actually caught defects.
        var majors = Prefixes(FullVersionsFile).Select(p => p.Split('.')[0]).Distinct().OrderBy(m => m);
        var prMajors = Prefixes(PrVersionsFile).Select(p => p.Split('.')[0]).Distinct().OrderBy(m => m);

        Assert.Equal(majors, prMajors);
    }

    [Fact]
    public void PullRequestList_KeepsEveryLegThatRunsTheCSharpSuite()
    {
        // `unit-tests` is "the newest prefix of each major", computed from the FULL list. If
        // the pull-request list dropped one of those, AlRunner.Tests would silently stop
        // running on that major for every pull request — and a 27-only C# regression is not
        // hypothetical: see this file's header for the run that caught one.
        var all = Prefixes(FullVersionsFile);
        var pr = Prefixes(PrVersionsFile);

        var unitPrefixes = all.GroupBy(p => p.Split('.')[0]).Select(g => NewestOf(g)).ToArray();

        Assert.Empty(unitPrefixes.Except(pr, StringComparer.Ordinal));
        Assert.True(unitPrefixes.Length >= 2,
            "the C# suite must still run on more than one BC major — one run of it cannot see a version split");
    }

    [Fact]
    public void PullRequestList_KeepsThePrimaryPrefix()
    {
        // resolve-versions emits `required-version` only for the primary prefix, and the
        // smoke job builds against it. A pull-request list without it would hand smoke an
        // empty version.
        var all = Prefixes(FullVersionsFile);

        Assert.Contains(NewestOf(all), Prefixes(PrVersionsFile));
    }

    // ---- how the shared matrix applies it ---------------------------------------------

    [Fact]
    public void SharedMatrix_NarrowsOnlyForAPullRequest()
    {
        // push, schedule and the release path must all still resolve the full list. The test
        // is that the pull-request list is read under a condition on the event name and
        // nowhere else, so a scheduled or release run cannot pick it up by accident.
        var step = CodeOnly(ResolveStep());

        // The guard: every mention of the pull-request list sits INSIDE the `if` that tests
        // the event name. One outside it would read the narrowed list on a push, a schedule
        // or a release — and none of those has anything that would notice.
        var guard = step.IndexOf("if [ \"${GATING_EVENT:-}\" = \"pull_request\" ]; then", StringComparison.Ordinal);
        Assert.True(guard > 0, "bc-tests.yml must gate the pull-request list on GATING_EVENT");

        var endOfGuard = step.IndexOf("\n          fi", guard, StringComparison.Ordinal);
        Assert.True(endOfGuard > guard, "could not find the end of the pull_request guard");

        var mentions = Regex.Matches(step, Regex.Escape(PrVersionsFile)).Select(m => m.Index).ToList();
        Assert.NotEmpty(mentions);
        Assert.All(mentions, i => Assert.InRange(i, guard, endOfGuard));
    }

    [Fact]
    public void SharedMatrix_PassesTheEventNameInAsAnEnvVar()
    {
        // Via env rather than a `${{ }}` expansion inside `run:` — the same reasoning the
        // bc-version-filter input already carries, since a `${{ }}` in a run body is textual
        // substitution before bash sees it.
        Assert.Contains("GATING_EVENT: ${{ github.event_name }}", Read("bc-tests.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedMatrix_DerivesPerLegAttributesFromTheFullList_BeforeAnyNarrowing()
    {
        // The property bc-leg-rerun.yml depends on, now also load-bearing for the
        // pull-request path: `required` and `unit-tests` are computed from the FULL list, so
        // a narrowed leg does exactly the work that leg does on a full run.
        var step = CodeOnly(ResolveStep());

        var primary = step.IndexOf("PRIMARY_PREFIX=", StringComparison.Ordinal);
        var unit = step.IndexOf("UNIT_PREFIXES=", StringComparison.Ordinal);
        var prNarrow = step.IndexOf(PrVersionsFile, StringComparison.Ordinal);
        var legNarrow = step.IndexOf("if [ -n \"${BC_VERSION_FILTER:-}\" ]", StringComparison.Ordinal);

        Assert.True(primary > 0 && unit > 0 && prNarrow > 0 && legNarrow > 0);
        Assert.True(primary < prNarrow && unit < prNarrow,
            "the primary/unit selections must be derived before the pull-request narrowing");
        Assert.True(primary < legNarrow && unit < legNarrow,
            "the primary/unit selections must be derived before the single-leg narrowing");
    }

    [Fact]
    public void SharedMatrix_RefusesAPullRequestListThatIsEmptyOrNotASubset()
    {
        // An unknown prefix must fail the run rather than resolving to an empty matrix — the
        // silent-no-op shape #1976/#2065 record. Same standard the single-leg filter already
        // holds itself to.
        var step = ResolveStep();

        Assert.Contains("::error::", step, StringComparison.Ordinal);
        var errors = Regex.Matches(step, @"::error::[^\n]*").Select(m => m.Value).ToList();
        Assert.Contains(errors, e => e.Contains(PrVersionsFile, StringComparison.Ordinal));
    }

    [Fact]
    public void SharedMatrix_FailsWhenTheResolvedMatrixIsEmpty()
    {
        // The mirror image, and the reason narrowing raises the stakes: an empty `target`
        // array means the `test` job never runs, and a workflow whose only real job never
        // ran still reports success.
        var step = ResolveStep();

        var errors = Regex.Matches(step, @"::error::[^\n]*").Select(m => m.Value).ToList();
        Assert.Contains(errors, e => e.Contains("empty matrix", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SharedMatrix_StillPublishesVersionsJsonForTheFullList()
    {
        // `pack` builds one engine variant per entry in versions-json and then asserts the
        // packed count equals the number of entries in bc-versions.txt. Narrowing that output
        // alongside the matrix would turn every pull request's pack job red, and relaxing the
        // assertion instead would ship a nupkg missing five engine variants.
        var step = CodeOnly(ResolveStep());
        var workflow = CodeOnly(Read("bc-tests.yml"));

        Assert.Contains("for prefix in $ALL_PREFIXES", step, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"versions-json=\$\{?ALL_ENTRIES\}?"), step);
        // The release-path assertion this protects, still reading the full list.
        Assert.Contains("expected=$(grep -v '^#' .github/bc-versions.txt", workflow, StringComparison.Ordinal);
    }

    // ---- main still gets all eight -----------------------------------------------------

    [Fact]
    public void TestMatrix_RunsTheFullMatrixOnASchedule()
    {
        // Narrowing pull requests is only defensible if something still runs the legs that
        // were dropped. Without this, a 28.2-only regression is caught never rather than
        // within the schedule interval.
        var triggers = WorkflowTriggers.TriggersOf(Read("test-matrix.yml"));

        Assert.Contains("schedule", triggers);
        Assert.Contains("pull_request", triggers);
        Assert.Contains("push", triggers);
        Assert.Matches(new Regex(@"cron:\s*'[^']+'"), Read("test-matrix.yml"));
    }

    [Fact]
    public void TestMatrix_ScheduledRunIsNotCancelledByAMergeToMain()
    {
        // A schedule fires on the default branch, so `github.ref` is the same
        // `refs/heads/main` a merge push carries. With cancel-in-progress: true and a group
        // keyed on ref alone, the next merge would kill the only run that covers the five
        // legs pull requests no longer run — the full matrix would then exist on paper only.
        var code = CodeOnly(Read("test-matrix.yml"));
        var group = Regex.Match(code, @"group:\s*(.+)");

        Assert.True(group.Success, "test-matrix.yml must still declare a concurrency group");
        Assert.Contains("github.event_name", group.Groups[1].Value, StringComparison.Ordinal);
    }

    // ---- the required-context rename ----------------------------------------------------

    [Fact]
    public void RequiredContext_IsNamedIdenticallyEverywhereThatNamesIt()
    {
        // #2785: two copies of this list already drifted apart once, and the copy that gates
        // the merge was the one left stale. A rename is exactly when that happens again.
        Assert.Contains($"name: {RequiredContext}", CodeOnly(Read("test-matrix.yml")),
            StringComparison.Ordinal);

        var guard = ReadRepo(Path.Combine(".github", "scripts", "check_required_contexts.py"));
        var defaults = Regex.Match(guard, @"DEFAULT_REQUIRED_CONTEXTS\s*=\s*\[([^\]]*)\]");
        Assert.True(defaults.Success, "check_required_contexts.py must still declare DEFAULT_REQUIRED_CONTEXTS");
        Assert.Contains($"\"{RequiredContext}\"", defaults.Groups[1].Value, StringComparison.Ordinal);

        var ciWait = ReadRepo(Path.Combine("tools", "ci-wait.py"));
        var ruleset = Regex.Match(ciWait, @"RULESET_CONTEXTS\s*=\s*\(([^)]*)\)");
        Assert.True(ruleset.Success, "ci-wait.py must still declare RULESET_CONTEXTS");
        Assert.Contains($"\"{RequiredContext}\"", ruleset.Groups[1].Value, StringComparison.Ordinal);

        // Both lists must also still agree on the OTHER required context, or the guard that
        // compares them against the live ruleset fails on drift it did not cause.
        Assert.Contains("\"Tests updated\"", defaults.Groups[1].Value, StringComparison.Ordinal);
        Assert.Contains("\"Tests updated\"", ruleset.Groups[1].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredContextName_IsDeclaredByNoWorkflowAtAll()
    {
        // A job still named "All BC versions passed" would report a second, unused context
        // and keep the false claim alive in `gh pr checks` output. The regex matches the job
        // -name FORM, so a comment explaining the rename does not trip it.
        var declared = Directory.GetFiles(Path.Combine(GithubDir, "workflows"), "*.yml")
            .Where(f => Regex.IsMatch(CodeOnly(File.ReadAllText(f)),
                @"name:\s*" + Regex.Escape(RetiredContext)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(declared);
    }

    [Fact]
    public void RequiredContext_IsDeclaredExactlyOnce_AndOnlyOnTheGatingPath()
    {
        // The same structural guarantee BcLegRerunWorkflowTests holds for the old name: one
        // declaration, in the workflow the ruleset actually gates, and none in the
        // diagnostic single-leg path where one leg's green would satisfy it.
        var declaring = Directory.GetFiles(Path.Combine(GithubDir, "workflows"), "*.yml")
            .Where(f => Regex.IsMatch(CodeOnly(File.ReadAllText(f)),
                @"name:\s*" + Regex.Escape(RequiredContext)))
            .Select(f => Path.GetFileName(f)!)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "test-matrix.yml" }, declaring);
    }
}
