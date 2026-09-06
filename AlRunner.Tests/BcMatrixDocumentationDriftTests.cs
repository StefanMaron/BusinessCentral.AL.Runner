// BcMatrixDocumentationDriftTests — issue #2883: documentation that states how many BC legs
// run, and which versions they are, must be checked against the files that decide it.
//
// #2883 was filed because four documents still described the al-language corpus CI as TWO BC
// legs (27.5 and 28.3) long after it had become eight. Nothing failed while they said it: a
// wrong leg count is invisible to every existing guard, and an agent reading "once both BC
// legs are green" will call a corpus PR ready with six legs unreported.
//
// The issue then went stale itself before it could be fixed, in the other direction. #3141 /
// #3200 narrowed AL RUNNER's own pull-request matrix to three legs, so "it is eight" stopped
// being a single answer here: the number now depends on the trigger. A correction written from
// the issue body would have shipped a fresh inaccuracy. That is the case for a guard rather
// than a careful reader — there are two independent version dials in this repository plus a
// third in the corpus submodule, and prose naming any of them rots silently.
//
// What each test measures, and against what:
//
//   * Version lists in prose            -> .github/bc-versions.txt, .github/pr-bc-versions.txt
//   * The corpus's eight-version claim  -> tests/al-language/.github/workflows/ci.yml
//   * "AlRunner.Tests runs on N legs"   -> the unit-test prefixes bc-tests.yml derives
//   * The aggregate required check name -> .github/workflows/test-matrix.yml
//
// PullRequestMatrixScopeTests is the sibling of this file on the workflow side: it holds the
// version FILES and the workflows honest to each other. This one holds the DOCUMENTS honest to
// the files. Neither subsumes the other — #2883 is a defect that lives entirely in prose.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcMatrixDocumentationDriftTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string GithubDir = Path.Combine(RepoRoot, ".github");

    /// <summary>
    /// The documents an agent reads to decide what CI measured. Deliberately the whole of
    /// `.claude/` and `docs/` rather than a named list: #2883's four stale spots were spread
    /// across a rule, an agent definition and a doc, and a named list is exactly the thing that
    /// would not have grown to cover the next one.
    /// </summary>
    private static IEnumerable<string> DocFiles()
    {
        foreach (var relRoot in new[] { "docs", ".claude" })
        {
            var root = Path.Combine(RepoRoot, relRoot);
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
            {
                // docs/archive/ is frozen by definition — it records what was true then.
                if (Rel(f).Replace('\\', '/').Contains("/archive/", StringComparison.Ordinal)) continue;
                yield return f;
            }
        }
        var readme = Path.Combine(RepoRoot, "README.md");
        if (File.Exists(readme)) yield return readme;
    }

    private static string Rel(string absolute) =>
        Path.GetRelativePath(RepoRoot, absolute).Replace('\\', '/');

    private static string[] Prefixes(string file)
    {
        var path = Path.Combine(GithubDir, file);
        Assert.True(File.Exists(path), $"expected {file} at {path}");
        return File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith('#'))
            .SelectMany(l => l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
    }

    /// <summary>
    /// A run of three or more BC version prefixes written out in prose — "27.0, 27.5 and 28.4",
    /// "27.3, 28.0, 28.1, 28.2 or 28.3". Three is the floor on purpose: a pair like "27.5 and
    /// 28.3" is overwhelmingly a past measurement ("green on BC 27.5 and 28.3"), which #2883
    /// itself calls out as legitimate and not to be touched.
    /// </summary>
    private static readonly Regex VersionRun =
        new(@"\b2[0-9]\.[0-9]+(?:\s*(?:,|,?\s*(?:and|or)|/)\s*\b2[0-9]\.[0-9]+){2,}", RegexOptions.Compiled);

    private static string[] MembersOf(string versionRun) =>
        Regex.Matches(versionRun, @"2[0-9]\.[0-9]+").Select(m => m.Value).ToArray();

    private static string Canonical(IEnumerable<string> versions) =>
        string.Join(' ', versions.Distinct(StringComparer.Ordinal).OrderBy(Version.Parse));

    /// <summary>
    /// Version runs that are NOT a claim about a matrix, keyed by (file, the run's canonical
    /// member set). Each needs a reason, and a dead entry fails
    /// <see cref="StaleVersionListAllowlist_HasNoDeadEntries"/> — an allowlist nobody prunes is
    /// how the next stale claim gets in wearing a waiver.
    /// </summary>
    private static readonly (string File, string Versions, string Why)[] NotAMatrixClaim =
    {
        ("docs/limitations.md", "27.0 27.3 27.5 28.1 28.2 28.4",
            "the BC artifacts that happened to be cached on the machine that measured the "
            + "install-seeding column check — a historical observation, not the matrix"),
        ("docs/upstream-corpus-workflow.md", "27.1 27.2 27.4",
            "the 27 minors the corpus does NOT run; naming the gap is the point of the sentence"),
    };

    // ---- version lists written out in prose --------------------------------------------

    /// <summary>
    /// Every BC version list written out in an agent-facing document must be one of the three
    /// sets the version files define: the full matrix, the pull-request subset, or the
    /// difference (the legs a PR does not run). Anything else is drift — including the shape
    /// #2883 was filed for, where a document names a matrix that has since grown.
    /// </summary>
    [Fact]
    public void VersionListsInDocs_NameOnlyASetDerivedFromTheVersionFiles()
    {
        var all = Canonical(Prefixes("bc-versions.txt"));
        var pr = Canonical(Prefixes("pr-bc-versions.txt"));
        var dropped = Canonical(Prefixes("bc-versions.txt").Except(Prefixes("pr-bc-versions.txt"), StringComparer.Ordinal));

        var allowed = new HashSet<string>(new[] { all, pr, dropped }, StringComparer.Ordinal);
        var waived = NotAMatrixClaim
            .Select(e => (e.File, e.Versions))
            .ToHashSet();

        var offenders = new List<string>();
        foreach (var file in DocFiles())
        {
            var rel = Rel(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in VersionRun.Matches(lines[i]))
                {
                    var canonical = Canonical(MembersOf(m.Value));
                    if (allowed.Contains(canonical)) continue;
                    if (waived.Contains((rel, canonical))) continue;
                    offenders.Add($"{rel}:{i + 1}: \"{m.Value}\" -> {{{canonical}}}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These documents write out a BC version list that is none of the three sets the "
            + "version files define. Either the document is stale (the #2883 shape) or the run "
            + "is a historical measurement that belongs in NotAMatrixClaim with a reason.\n"
            + $"  full matrix (.github/bc-versions.txt):    {all}\n"
            + $"  pull request (.github/pr-bc-versions.txt): {pr}\n"
            + $"  a PR does not run:                        {dropped}\n"
            + string.Join('\n', offenders.Select(o => "  " + o)));
    }

    /// <summary>
    /// A waiver that no longer matches anything is a waiver nobody re-read. Fail rather than
    /// carry it, so the list above cannot silently become permission for a claim that has since
    /// changed. (Same enforcement shape as BaseAppFloorFixtureGuardTests' allowlist.)
    /// </summary>
    [Fact]
    public void StaleVersionListAllowlist_HasNoDeadEntries()
    {
        foreach (var (file, versions, why) in NotAMatrixClaim)
        {
            var path = Path.Combine(RepoRoot, file);
            Assert.True(File.Exists(path), $"NotAMatrixClaim names {file}, which does not exist.");
            var found = VersionRun.Matches(File.ReadAllText(path))
                .Any(m => Canonical(MembersOf(m.Value)) == versions);
            Assert.True(found,
                $"NotAMatrixClaim waives \"{versions}\" in {file} ({why}), but no such version "
                + "list is there any more. Delete the entry.");
        }
    }

    // ---- the corpus's own matrix, read out of the submodule ----------------------------

    /// <summary>
    /// #2883's original defect, guarded: `docs/upstream-corpus-workflow.md` states which BC
    /// versions the corpus CI runs, and that statement must equal the `include` list in the
    /// corpus's own `ci.yml` at the pin this repository carries. It said two versions for long
    /// enough that agents acted on it.
    /// </summary>
    [SkippableFact]
    public void CorpusVersionClaim_MatchesTheSubmodulesOwnWorkflow()
    {
        var corpusCi = Path.Combine(RepoRoot, "tests", "al-language", ".github", "workflows", "ci.yml");
        Skip.IfNot(File.Exists(corpusCi),
            "tests/al-language is not checked out here — nothing to compare the claim against.");

        // The matrix the corpus dispatches when no workflow_dispatch override is given: the
        // else-branch JSON in its `prepare` job. Read the bc_version values out of it rather
        // than counting `include` entries, so an added version is caught by NAME.
        var text = File.ReadAllText(corpusCi);
        var elseBranch = text[(text.IndexOf("\n          else\n", StringComparison.Ordinal) + 1)..];
        var corpusVersions = Regex.Matches(elseBranch, @"""bc_version"":""(2[0-9]\.[0-9]+)""")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.True(corpusVersions.Length >= 2,
            $"could not read the corpus matrix out of {Rel(corpusCi)} — the guard would pass "
            + "vacuously, which is the failure it exists to prevent.");

        var doc = Path.Combine(RepoRoot, "docs", "upstream-corpus-workflow.md");
        var docText = File.ReadAllText(doc);
        var expected = Canonical(corpusVersions);
        var stated = VersionRun.Matches(docText)
            .Select(m => Canonical(MembersOf(m.Value)))
            .ToArray();

        Assert.True(stated.Contains(expected),
            $"docs/upstream-corpus-workflow.md must write out the corpus's own matrix "
            + $"({expected}) — that is the list an agent counts green legs against. "
            + $"Version lists it does state: {(stated.Length == 0 ? "(none)" : string.Join(" | ", stated))}");

        // And the count claim next to it. "eight" is a word, not a number, in that sentence.
        var numberWords = new Dictionary<int, string>
        {
            [2] = "two", [3] = "three", [4] = "four", [5] = "five",
            [6] = "six", [7] = "seven", [8] = "eight", [9] = "nine", [10] = "ten",
        };
        Assert.True(numberWords.TryGetValue(corpusVersions.Length, out var word),
            $"the corpus now runs {corpusVersions.Length} versions — extend numberWords.");
        var flowed = Regex.Replace(docText, @"\s+", " ");
        Assert.Contains($"**{word} BC versions", flowed, StringComparison.Ordinal);
    }

    // ---- what runs on a leg, as opposed to how many legs there are ---------------------

    /// <summary>
    /// `AlRunner.Tests` runs on the UNIT legs only — the newest minor of each major, derived in
    /// bc-tests.yml from the full version list (#2674, re-measured by #3141). A document
    /// claiming the C# suite runs on every leg tells an agent a red C# test will be caught by
    /// whichever leg it happens to read, and on a pull request that is wrong twice over: three
    /// legs run, two of them carry the suite.
    /// </summary>
    [Fact]
    public void AgentDocs_DoNotClaimTheCSharpSuiteRunsOnEveryBcLeg()
    {
        var claimsEveryLeg = new Regex(
            @"(each|all|every)\s+(of\s+)?(the\s+)?(\d+|two|three|four|five|six|seven|eight)?\s*legs?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in DocFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var idx = lines[i].IndexOf("AlRunner.Tests", StringComparison.Ordinal);
                if (idx < 0) continue;
                // Only the text that follows the suite's name on that line — an unrelated
                // earlier clause about legs is not a claim about the suite.
                var after = lines[i][idx..];
                var m = claimsEveryLeg.Match(after);
                if (m.Success) offenders.Add($"{Rel(file)}:{i + 1}: \"{m.Value.Trim()}\"");
            }
        }

        var unit = Canonical(Prefixes("bc-versions.txt")
            .GroupBy(p => p.Split('.')[0])
            .Select(g => g.OrderBy(Version.Parse).Last()));

        Assert.True(offenders.Count == 0,
            $"AlRunner.Tests runs on the unit legs only ({unit}) — the newest minor of each "
            + "major, two legs, not every leg of whichever matrix ran. These lines claim "
            + "otherwise:\n" + string.Join('\n', offenders.Select(o => "  " + o)));
    }

    /// <summary>
    /// The positive half of the test above: the impl-agent definition — the one document every
    /// implementation agent loads before it pushes — must name the legs a pull request actually
    /// runs and the legs that carry the C# suite, both derived from the version files. Without
    /// this, deleting the sentence would satisfy the negative test.
    /// </summary>
    [Fact]
    public void ImplAgentDefinition_NamesThePullRequestLegsAndTheUnitLegs()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, ".claude", "agents", "impl-agent.md"));

        var pr = Prefixes("pr-bc-versions.txt");
        var unit = Prefixes("bc-versions.txt")
            .GroupBy(p => p.Split('.')[0])
            .Select(g => g.OrderBy(Version.Parse).Last())
            .OrderBy(Version.Parse)
            .ToArray();

        var runs = VersionRun.Matches(text).Select(m => Canonical(MembersOf(m.Value))).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(Canonical(pr), runs);

        // The unit legs are a pair, which VersionRun deliberately does not match, so assert the
        // pair literally instead of widening a regex that would then swallow every historical
        // "green on BC 27.5 and 28.3".
        Assert.Contains($"{unit[0]} and {unit[1]}", text, StringComparison.Ordinal);

        Assert.Contains(".github/pr-bc-versions.txt", text, StringComparison.Ordinal);
        Assert.Contains(".github/bc-versions.txt", text, StringComparison.Ordinal);
    }

    // ---- the aggregate required check, by name -----------------------------------------

    /// <summary>
    /// #3200 renamed the aggregate required check because a narrowed matrix under the old name
    /// asserted something the run had not measured. A document still naming the old one sends an
    /// agent looking for a context that no longer reports — indistinguishable, from the outside,
    /// from a check that has not started yet.
    /// </summary>
    [Fact]
    public void NoDocumentNamesTheRetiredAggregateCheck()
    {
        // Sourced from the workflow, so this cannot pin a name the ruleset no longer requires.
        var matrix = File.ReadAllText(Path.Combine(GithubDir, "workflows", "test-matrix.yml"));
        var m = Regex.Match(matrix, @"^\s*name:\s*(BC test matrix passed|All BC versions passed)\s*$",
            RegexOptions.Multiline);
        Assert.True(m.Success, "test-matrix.yml must still name its aggregate job.");
        var current = m.Groups[1].Value;
        // The name this replaced. Written out because a guard for a retired string needs the
        // string; ReleaseTestParityTests scans .github/workflows/ only, so a mention here is
        // inert. Docs are what this test is about, and they may not carry it.
        var retired = current == "BC test matrix passed" ? "All BC versions passed" : "BC test matrix passed";

        var offenders = DocFiles()
            .Where(f => File.ReadAllText(f).Contains(retired, StringComparison.Ordinal))
            .Select(Rel)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"The aggregate required check is \"{current}\"; \"{retired}\" is retired and no "
            + "longer reports. Named in: " + string.Join(", ", offenders));
    }
}
