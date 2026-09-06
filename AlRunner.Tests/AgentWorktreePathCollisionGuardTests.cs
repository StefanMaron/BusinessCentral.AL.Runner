using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Guards the invariant #3014 established: an agent's worktree path must be derived from the
/// SAME two components as its branch name — the identity AND the issue number — so that two
/// agents sharing an identity but working different issues cannot land in the same directory.
///
/// The incident (#3014): two autonomous loops both claimed the slot <c>stma-auto-1</c>. Their
/// branch names did NOT collide — <c>agent/stma-auto-1/issue-3005</c> and
/// <c>agent/stma-auto-1/issue-3011</c> are distinct, because a branch name carries the issue
/// number. The WORKTREE PATH collided, because <c>.claude/agents/impl-agent.md</c> derived it
/// from the identity alone: both loops rendered <c>.claude/worktrees/stma-auto-1</c>. A
/// directory is what <c>git commit</c> and <c>git push</c> consult to decide which branch they
/// act on, so the second loop's two commits (<c>6c36e717</c>, <c>67762959</c> — 554 added lines
/// across 9 files, belonging to a different issue entirely) landed on the first loop's PR
/// branch. CI run <c>34003048066</c> then reported Test Matrix SUCCESS on head <c>67762959</c>:
/// a green required-check verdict on the union of two unrelated changes.
///
/// Nothing objected at any layer, and nothing could have: both loops push as the same account,
/// so the author field is identical, the branch prefix is identical, and the commits are
/// well-formed. The asymmetry between "branch name carries the issue number" and "worktree path
/// does not" was the entire mechanism.
///
/// This is enforced here rather than described in a rule because the prose form of exactly this
/// requirement already existed and was not enough. <c>.claude/skills/autonomous-cycle/SKILL.md</c>
/// says "Two contributors must never write to the same path or push to the same branch" — and
/// the violation arrived anyway. That is the same failure shape
/// <see cref="ScratchDirOwnershipGuardTests"/> records: an allowlist written as prose with no
/// test behind it was ignored.
///
/// Scope is <c>.claude/agents/</c> — the directory whose files PRESCRIBE a worktree to create,
/// rather than a hard-coded filename, so a future agent definition inherits the guard. Other
/// mentions of <c>.claude/worktrees/</c> elsewhere in the repository (e.g.
/// <c>.claude/rules/no-git-stash-with-worktrees.md</c>'s <c>impl-*</c> glob) DESCRIBE existing
/// directories and are deliberately out of scope.
///
/// Within those files, only a <b>template</b> is asserted over — a path token carrying at least
/// one <c>&lt;...&gt;</c> placeholder, which is what makes it something an agent renders. A fully
/// concrete token such as <c>.claude/worktrees/stma-auto-1</c> is a citation of one historical
/// directory, and this file's own summary above contains exactly that; the section of
/// <c>impl-agent.md</c> that explains the incident does too. Flagging those would be the failure
/// <see cref="ScratchDirOwnershipGuardTests"/> names — a guard that fires on prose quoting the
/// thing it forbids trains readers to ignore it. The distinction is not a loophole: a
/// prescription has to tell the agent which identity to substitute, so it cannot be written
/// without a placeholder and still be usable.
/// </summary>
public sealed class AgentWorktreePathCollisionGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string AgentsDir => Path.Combine(RepoRoot, ".claude", "agents");

    /// <summary>The issue-number placeholder an agent definition uses in a branch name.</summary>
    private const string IssuePlaceholder = "<N>";

    /// <summary>
    /// Every <c>.claude/worktrees/...</c> path token written in an agent definition, paired with
    /// the file and line it came from. The trailing character class stops at whatever ends a path
    /// in markdown prose or a fenced command — whitespace, a backtick, a quote or a bracket.
    /// </summary>
    private static bool IsTemplate(string token) => token.Contains('<');

    /// <summary>Templates only — the tokens an agent is expected to render.</summary>
    private static List<(string File, int Line, string Token)> PrescribedTemplates() =>
        WorktreePathTokens().Where(t => IsTemplate(t.Token)).ToList();

    private static IEnumerable<(string File, int Line, string Token)> WorktreePathTokens()
    {
        var rx = new Regex(@"\.claude/worktrees/([^\s`""'’)\]]+)");
        foreach (var path in Directory.EnumerateFiles(AgentsDir, "*.md", SearchOption.AllDirectories)
                                      .OrderBy(p => p, StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in rx.Matches(lines[i]))
                {
                    yield return (Path.GetRelativePath(RepoRoot, path), i + 1, m.Groups[1].Value);
                }
            }
        }
    }

    /// <summary>
    /// The guard proper. Every prescribed worktree path must carry the issue-number placeholder,
    /// so it cannot be rendered without one.
    /// </summary>
    [Fact]
    public void EveryPrescribedWorktreePathCarriesTheIssueNumber()
    {
        var tokens = PrescribedTemplates();

        Assert.True(tokens.Count > 0,
            $"no `.claude/worktrees/<...>` TEMPLATE found under {AgentsDir}. Either the " +
            "agent definitions stopped prescribing a worktree, or this guard's regex drifted — " +
            "either way it is no longer guarding anything. " +
            $"({WorktreePathTokens().Count()} worktree path token(s) were seen in total.)");

        var offenders = tokens
            .Where(t => !t.Token.Contains(IssuePlaceholder, StringComparison.Ordinal))
            .Select(t => $"  {t.File}:{t.Line}  .claude/worktrees/{t.Token}")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} of {tokens.Count} prescribed worktree path(s) omit the " +
            $"`{IssuePlaceholder}` issue placeholder, so two agents sharing an identity but " +
            "working different issues render the SAME directory — the #3014 mechanism, which " +
            "put one loop's commits on another loop's PR branch and got a green CI verdict on " +
            "the mixture. Derive the path from identity AND issue, mirroring the branch name " +
            "`agent/<AGENT-ID>/issue-<N>`:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The incident itself, rendered. Two DIFFERENT issues under the SAME identity must produce
    /// two different directories. This is the assertion that fails against the pre-fix wording:
    /// both sides render <c>.claude/worktrees/stma-auto-1</c>.
    /// </summary>
    [Fact]
    public void SameIdentityDifferentIssuesRenderDifferentWorktrees()
    {
        const string Identity = "stma-auto-1";

        foreach (var (file, line, token) in PrescribedTemplates())
        {
            var a = Render(token, Identity, "3005");
            var b = Render(token, Identity, "3011");

            Assert.True(a != b,
                $"{file}:{line}: the documented worktree path `.claude/worktrees/{token}` " +
                $"renders to `{a}` for BOTH issue 3005 and issue 3011 under identity " +
                $"'{Identity}'. That is exactly the #3014 collision: one loop committed and " +
                "pushed into the other's checkout, onto the other's branch.");
        }
    }

    /// <summary>
    /// The converse, so the fix cannot be satisfied by a path that varies ONLY by issue and
    /// drops the identity — that would collide across identities instead, which is the same
    /// defect wearing the other hat.
    /// </summary>
    [Fact]
    public void DifferentIdentitiesSameIssueRenderDifferentWorktrees()
    {
        foreach (var (file, line, token) in PrescribedTemplates())
        {
            var a = Render(token, "stma-auto-1", "3005");
            var b = Render(token, "stma-auto-2", "3005");

            Assert.True(a != b,
                $"{file}:{line}: the documented worktree path `.claude/worktrees/{token}` " +
                $"renders to `{a}` for both identity 'stma-auto-1' and 'stma-auto-2' on issue " +
                "3005, so two loops on DIFFERENT slots would share a checkout.");
        }
    }

    private static string Render(string token, string identity, string issue) =>
        token.Replace("<AGENT-ID>", identity, StringComparison.Ordinal)
             .Replace(IssuePlaceholder, issue, StringComparison.Ordinal);
}
