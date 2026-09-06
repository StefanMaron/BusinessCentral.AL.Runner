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
// required contexts. `main`'s ruleset requires exactly `All BC versions passed` and
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

        Assert.Contains("--count-baseline", body);
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
        // failures; without --count-baseline a suite that stops being discovered is silent.
        Assert.Contains("--strict", body);
        Assert.Contains("test-count-baseline.json", body);
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
    /// The sibling of <c>CountBaselineMergeShapeTests.RunnerExtrasGroupKeys_AreExactlyTheAppGroupDirectories</c>,
    /// which the corpus had no equivalent of. Now that every corpus app runs, every corpus app
    /// must also carry a count baseline — otherwise a newly-executed app's test count is
    /// unguarded, and it can later stop being discovered exactly as quietly as before:
    /// <c>CountBaselineCheck</c> imposes no expectation on a suite the manifest never names.
    /// </summary>
    [Fact]
    public void EveryCorpusAppHasACountBaselineEntry()
    {
        var corpusRoot = Path.Combine(RepoRoot, "tests", "al-language");
        var appDirs = EnumerateCorpusApps(corpusRoot);
        if (appDirs.Count == 0)
        {
            // The submodule is not checked out (a bare `git clone` without --recursive).
            // Asserting here would fail for a reason that has nothing to do with the
            // baseline; CI always checks it out, and the workflow's own enumeration exits 1
            // with the `git submodule update --init` hint when it is missing.
            Assert.False(Directory.Exists(Path.Combine(corpusRoot, "tests")),
                $"{corpusRoot} is checked out but no app directory was found under it — "
                + "the enumeration rule and the corpus layout have diverged.");
            return;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(
                RepoRoot, "tests", "expectations", "count-baseline", "test-count-baseline.json")));
        var declared = doc.RootElement.GetProperty("suites").EnumerateObject()
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var missing = appDirs.Select(Path.GetFileName)
            .Where(n => !declared.Contains(n!))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "tests/expectations/count-baseline/test-count-baseline.json has no suite entry for "
            + string.Join(", ", missing)
            + ".\nThe corpus leg now runs every corpus app as its own bundle root, and the suite key "
            + "is the app directory's basename. Add one line per app — a dependency-only app with no "
            + "tests of its own is { \"tests\": { \"default\": 0 }, \"appGroups\": { \"default\": 1 } } "
            + "and still counts.");
    }

    /// <summary>
    /// The same rule <c>scripts/corpus-app-dirs.py</c> implements and
    /// <c>ProgramSupport.LooksLikeSuite</c> defines: a directory that declares its own
    /// app.json, or uses the src//test/ split, is one app, and descent stops there.
    /// </summary>
    private static List<string> EnumerateCorpusApps(string root)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        if (LooksLikeApp(root)) { found.Add(root); return found; }

        void Descend(string dir)
        {
            foreach (var child in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (Path.GetFileName(child).StartsWith('.')) continue;
                if (LooksLikeApp(child)) found.Add(child);
                else Descend(child);
            }
        }

        Descend(root);
        return found;
    }

    private static bool LooksLikeApp(string dir)
        => File.Exists(Path.Combine(dir, "app.json"))
        || Directory.Exists(Path.Combine(dir, "src"))
        || Directory.Exists(Path.Combine(dir, "test"));
}
