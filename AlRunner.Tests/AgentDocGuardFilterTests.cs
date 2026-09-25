using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// The `--filter "FullyQualifiedName~GuardTests"` command `.claude/agents/impl-agent.md` tells
/// every implementation agent to run must select tests. `dotnet test` answers a filter matching
/// nothing with exit 0 (tdd.md), so a renamed suite would turn that step into a silent pass.
///
/// Until #4248 this pinned the doc's `# N tests` figure. Every PR adding a guard test edited it
/// the same way, identical hunks merge cleanly, and two such PRs red `main` together; the figure
/// is gone on the owner's direction that counts which change without anyone editing the sentence
/// belong in no durable text (#4539).
/// </summary>
public sealed class AgentDocGuardFilterTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string Doc => Path.Combine(RepoRoot, ".claude", "agents", "impl-agent.md");

    private const string Filter = "GuardTests";

    private static readonly Regex DocumentedFilterRx = new(
        @"--filter\s+""FullyQualifiedName~(?<f>[A-Za-z0-9_.]+)""", RegexOptions.Compiled);

    [Fact]
    public void ImplAgentDocsGuardFilter_SelectsAtLeastOneTest()
    {
        Assert.True(File.Exists(Doc), $"impl-agent.md not found: {Doc}");
        var documented = DocumentedFilterRx.Matches(File.ReadAllText(Doc))
            .Select(m => m.Groups["f"].Value)
            .ToList();

        // Third state: the command reworded away means this test checks nothing.
        Assert.True(
            documented.Contains(Filter),
            $"UNMEASURABLE: impl-agent.md no longer documents `--filter \"FullyQualifiedName~{Filter}\"` "
            + $"(found: [{string.Join(", ", documented)}]). Re-point this test at the documented "
            + "filter; do not delete the assertion to make the suite green.");

        var matching = typeof(AgentDocGuardFilterTests).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes(inherit: true).Any(a => a is FactAttribute))
                .Select(m => $"{t.FullName}.{m.Name}"))
            .Where(name => name.Contains(Filter, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            matching.Count > 0,
            $"impl-agent.md tells every agent to run `--filter \"FullyQualifiedName~{Filter}\"`, and "
            + "no test in this assembly matches it — `dotnet test` would report success having run "
            + "nothing. Point the doc at the suite's current name.");
    }
}
