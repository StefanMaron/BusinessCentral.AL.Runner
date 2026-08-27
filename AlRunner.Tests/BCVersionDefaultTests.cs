using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #2102: a bare `dotnet build`/`dotnet test` with no `-p:_BCVersion` failed with
/// CS1705 because AlRunner.csproj and AlRunner.Tests.csproj each declared their OWN
/// default BC build (28.1.49838.53910 vs 28.1.49838.50794) and a project-level MSBuild
/// property never flows to a sibling project. A command-line `-p:_BCVersion` is a global
/// property that overrides both, so every CI leg (which always passes it) never saw the
/// mismatch — only a bare local build did.
///
/// The fix is structural, not a value bump: `_BCVersion` is declared exactly ONCE, in the
/// repo-root Directory.Build.props that every project already implicitly imports, kept
/// under the same `Condition="'$(_BCVersion)' == ''"` form so an explicit `-p:_BCVersion`
/// still wins everywhere it does today. These tests are the drift gate that keeps that
/// true — they fail the moment a second per-project default reappears (the actual defect
/// this issue reported), or the shared declaration loses its override guard.
/// </summary>
public class BCVersionDefaultTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void NoProjectFile_DeclaresItsOwnBCVersionDefault()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            // tests/al-language is a read-only upstream submodule; not ours to police,
            // and it declares no BC-version defaults of its own anyway.
            var rel = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
            if (rel.StartsWith("tests/al-language/", StringComparison.Ordinal)) continue;

            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("<!--", StringComparison.Ordinal)) continue; // comment, not a live declaration
                if (Regex.IsMatch(trimmed, @"^<_BCVersion\b"))
                    offenders.Add($"{rel}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Only Directory.Build.props may declare a _BCVersion default. A per-project "
            + "default drifts from the shared one exactly like AlRunner.csproj and "
            + "AlRunner.Tests.csproj did (#2102): a bare local build silently picks up "
            + "whichever project MSBuild happens to evaluate first, and mismatched "
            + "defaults across projects break with CS1705. Offending declarations:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void DirectoryBuildProps_DeclaresExactlyOneConditionalBCVersionDefault()
    {
        var path = Path.Combine(RepoRoot, "Directory.Build.props");
        var text = File.ReadAllText(path);

        var matches = Regex.Matches(text, @"<_BCVersion\b[^>]*>[^<]*</_BCVersion>");
        Assert.True(matches.Count == 1,
            $"Directory.Build.props must declare the shared _BCVersion default exactly "
            + $"once so it cannot drift from itself; found {matches.Count} declaration(s).");

        var declaration = matches[0].Value;
        Assert.Contains("Condition=\"'$(_BCVersion)' == ''\"", declaration);
        // Must actually be pinned to a full 4-part BC build, not left blank — an unpinned
        // default is a different bug (every dev resolves a different "latest").
        Assert.Matches(@">\d+\.\d+\.\d+\.\d+<", declaration);
    }
}
