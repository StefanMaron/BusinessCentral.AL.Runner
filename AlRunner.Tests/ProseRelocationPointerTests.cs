// ProseRelocationPointerTests — issue #3260: when explanatory prose moves out of AlRunner/ into
// docs/, the pointer left behind is the only thing connecting a reader to it, and a pointer can
// rot in three ways with nothing failing.
//
// The maintainer's decision on #3260 names the drift test as the load-bearing part of moving
// prose out: "the failure mode when prose moves out is not 'the doc is far away', it is 'the doc
// silently stops matching the code'". This holds the pointers themselves honest — that the file
// exists, that a named #anchor is really in it, and that a doc written to hold one file's
// derivation still has that file pointing at it.
//
// It is deliberately NOT a check that the prose is accurate; nothing can check that. It checks
// the cheap half that is currently unchecked and that breaks first: a renamed doc, a removed
// anchor, an orphaned relocation.
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class ProseRelocationPointerTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// Every `docs/<file>.md` or `docs/<file>.md#anchor` mention in a C# source file under
    /// `AlRunner/`, with the file it was found in. Matches inside comments and string literals
    /// alike: a doc link held in a `const string` (the shape
    /// `RecordPatches.ObjectMetadataSystemTable` uses for its refusals) is a pointer that can rot
    /// exactly like one in a comment.
    /// </summary>
    private static IEnumerable<(string Source, string Doc, string? Anchor)> Pointers()
    {
        var root = Path.Combine(RepoRoot, "AlRunner");
        // The anchor must not swallow a sentence-ending period, and it must not end in one:
        // `docs/x.md#a-b.` in running prose is a pointer to `#a-b`, not to `#a-b.`. Measured —
        // the first draft of this regex reported three false failures for exactly that reason.
        var rx = new Regex(@"docs/(?<file>[A-Za-z0-9._-]+\.md)(#(?<anchor>[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*))?",
            RegexOptions.Compiled);
        foreach (var cs in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(RepoRoot, cs).Replace('\\', '/');
            if (rel.Contains("/obj/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/bin/", StringComparison.Ordinal)) continue;
            foreach (Match m in rx.Matches(File.ReadAllText(cs)))
            {
                var anchor = m.Groups["anchor"].Success ? m.Groups["anchor"].Value : null;
                // `docs/scope.md#anchor` in RunnerOutOfScopeException is prose describing the
                // SHAPE of a link the parser strips, not a link to follow. A placeholder that
                // named a real section would be worse, so it is excluded rather than "fixed".
                if (anchor == "anchor") anchor = null;
                yield return (rel, m.Groups["file"].Value, anchor);
            }
        }
    }

    [Fact]
    public void EveryDocPointerFromAlRunnerNamesAFileThatExists()
    {
        var missing = Pointers()
            .Where(p => !File.Exists(Path.Combine(RepoRoot, "docs", p.Doc)))
            .Select(p => $"{p.Source} -> docs/{p.Doc}")
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        Assert.True(missing.Count == 0,
            "A code comment points at a docs/ file that does not exist. Prose moved out of "
            + "AlRunner/ is reachable only through its pointer, so a renamed or deleted doc "
            + "silently strands it:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void EveryAnchoredDocPointerNamesAnAnchorThatExists()
    {
        var broken = new List<string>();
        foreach (var p in Pointers().Where(p => p.Anchor != null).Distinct())
        {
            var path = Path.Combine(RepoRoot, "docs", p.Doc);
            if (!File.Exists(path)) continue;   // the sibling test above owns that failure
            if (!DocHasAnchor(File.ReadAllText(path), p.Anchor!))
                broken.Add($"{p.Source} -> docs/{p.Doc}#{p.Anchor}");
        }

        Assert.True(broken.Count == 0,
            "A code comment points at a docs/ anchor that is not in that file. The pointer still "
            + "resolves to the document, so nothing looks broken, and the reader lands on the "
            + "wrong section:\n  " + string.Join("\n  ", broken.Distinct().OrderBy(s => s)));
    }

    /// <summary>
    /// An anchor exists if the document declares it explicitly (<c>&lt;a id="..."&gt;</c>) or if
    /// a markdown heading slugifies to it. Both spellings are in use under `docs/` — the explicit
    /// form in `limitations.md`, heading slugs everywhere else — so accepting only one would fail
    /// on live, correct pointers.
    /// </summary>
    private static bool DocHasAnchor(string markdown, string anchor)
    {
        if (markdown.Contains($"<a id=\"{anchor}\"", StringComparison.OrdinalIgnoreCase)) return true;
        if (markdown.Contains($"<a name=\"{anchor}\"", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var line in markdown.Split('\n'))
        {
            var t = line.TrimStart();
            if (!t.StartsWith('#')) continue;
            if (string.Equals(Slug(t.TrimStart('#').Trim()), anchor, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>GitHub's heading-to-anchor rule: lowercase, drop punctuation, spaces to dashes.</summary>
    private static string Slug(string heading)
    {
        var chars = heading.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_')
            .Select(c => c == ' ' ? '-' : c);
        return new string(chars.ToArray());
    }

    /// <summary>
    /// A doc created to hold one file's relocated derivation is orphaned the moment nothing points
    /// at it — the prose is then unreachable from the code it explains, which is the failure this
    /// whole file exists to prevent. Named explicitly rather than inferred: a general "every doc
    /// must be referenced" rule would wrongly fail the many docs/ files that are entry points in
    /// their own right (scope.md, limitations.md, expectations.md).
    /// </summary>
    [Theory]
    [InlineData("blob-store-isolation.md", "AlRunner/Patches/BlobStoreIsolationPatches.cs")]
    public void ARelocationDocIsStillPointedAtByTheFileItWasSplitFrom(string doc, string source)
    {
        Assert.True(File.Exists(Path.Combine(RepoRoot, "docs", doc)),
            $"docs/{doc} is missing; {source} was reduced on the promise that it exists.");

        var text = File.ReadAllText(Path.Combine(RepoRoot, source));
        Assert.True(text.Contains($"docs/{doc}", StringComparison.Ordinal),
            $"{source} no longer points at docs/{doc}. The derivation was moved out of that file "
            + "and is now unreachable from it — either restore the pointer or fold the prose back in.");
    }
}
