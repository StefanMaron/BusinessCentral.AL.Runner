using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The rules in .claude/rules/ are auto-loaded and cross-reference each other by
/// filename, mostly under a "Sister rules" heading. Nothing validated those
/// references, so a rule renamed or deleted left dangling pointers in every file
/// that named it — and an agent following a pointer to a file that does not exist
/// gets nothing, silently, exactly when it was trying to find the constraint that
/// applies to what it is about to do.
///
/// #2171 added a rule with four such references, which is what surfaced the gap.
/// This pins every reference in the whole directory, not just that file's.
/// </summary>
public sealed class RuleCrossReferenceTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string RulesDir => Path.Combine(RepoRoot, ".claude", "rules");

    // Matches a bare rule filename in backticks, e.g. `no-assumption-fixes.md`, and
    // the markdown-link form used in a few files, e.g. [`tdd.md`](tdd.md). Both are
    // pointers an agent is expected to be able to follow.
    private static readonly Regex RuleRefRx = new(
        @"`(?<name>[a-z0-9][a-z0-9._-]*\.md)`", RegexOptions.Compiled);

    [Fact]
    public void EveryRuleFileCrossReferenceNamesAFileThatExists()
    {
        Assert.True(Directory.Exists(RulesDir), $"rules directory not found: {RulesDir}");

        var ruleFiles = Directory.GetFiles(RulesDir, "*.md").OrderBy(f => f).ToList();
        Assert.NotEmpty(ruleFiles);

        // Names that live outside .claude/rules/ but are legitimately referenced from
        // inside it. Anything not in this set must resolve to a real rule file.
        var knownOutsideRules = new HashSet<string>(StringComparer.Ordinal)
        {
            "README.md",
            "CHANGELOG.md",
            "CLAUDE.md",
            "app.json",
        };

        var present = new HashSet<string>(
            ruleFiles.Select(Path.GetFileName)!, StringComparer.Ordinal);

        var dangling = new List<string>();
        foreach (var file in ruleFiles)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in RuleRefRx.Matches(text))
            {
                var name = m.Groups["name"].Value;
                if (knownOutsideRules.Contains(name)) continue;
                // A path-qualified reference (docs/expectations.md, ../../tests/...)
                // points outside this directory on purpose; only bare names are
                // claims about a sibling rule.
                if (name.Contains('/')) continue;
                if (!present.Contains(name))
                    dangling.Add($"{Path.GetFileName(file)} -> {name}");
            }
        }

        Assert.True(
            dangling.Count == 0,
            "rule files reference sibling rules that do not exist:\n  " +
            string.Join("\n  ", dangling.Distinct().OrderBy(s => s)));
    }

    /// <summary>
    /// Negative direction: the check above must actually fail on a dangling
    /// reference. Without this, a regex that silently matches nothing would make
    /// the test vacuously green forever.
    /// </summary>
    [Fact]
    public void TheCrossReferenceCheckRejectsANameThatDoesNotResolve()
    {
        var present = new HashSet<string>(
            Directory.GetFiles(RulesDir, "*.md").Select(Path.GetFileName)!,
            StringComparer.Ordinal);

        const string sample = "See `no-assumption-fixes.md` and `this-rule-does-not-exist.md`.";
        var names = RuleRefRx.Matches(sample).Select(m => m.Groups["name"].Value).ToList();

        Assert.Equal(
            new[] { "no-assumption-fixes.md", "this-rule-does-not-exist.md" }, names);
        Assert.Contains("no-assumption-fixes.md", present);
        Assert.DoesNotContain("this-rule-does-not-exist.md", present);
    }

    /// <summary>
    /// The rule #2171 adds is only useful if it is discoverable from the rules an
    /// agent is already reading when the question comes up. Pin those three
    /// entry points so a future edit cannot quietly orphan it.
    /// </summary>
    [Fact]
    public void TheAskTheCorpusRuleIsReachableFromItsSisterRules()
    {
        const string target = "ask-the-corpus-before-claiming-bc-behavior.md";
        Assert.True(File.Exists(Path.Combine(RulesDir, target)), $"{target} is missing");

        foreach (var sister in new[]
                 {
                     "bc-behavior-tests-go-upstream.md",
                     "no-assumption-fixes.md",
                     "al-language-submodule.md",
                 })
        {
            var path = Path.Combine(RulesDir, sister);
            Assert.True(File.Exists(path), $"{sister} is missing");
            Assert.Contains(target, File.ReadAllText(path));
        }
    }
}
