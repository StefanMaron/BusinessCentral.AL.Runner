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
///
/// Issue #3139: the scan below used to be
/// `Directory.EnumerateFiles(RepoRoot, "*.csproj", SearchOption.AllDirectories)` with a
/// single `tests/al-language/` prefix skip, which walked straight into `.claude/worktrees/`.
/// Every concurrent agent keeps a FULL checkout of this repository there, so the guard read
/// 126 project files on a developer box against the repository's own 6 — 95% of its input was
/// other people's branches. It was latent rather than live (nothing there declared
/// `_BCVersion` at the time), and that is the problem, not the reprieve: the verdict depended
/// on what other agents happened to have checked out. Worse, `.claude/worktrees/` is
/// gitignored, so it does not exist on a CI runner — the guard was wrong locally and right in
/// CI, the one combination no CI leg can ever surface. The walk is now explicit about what it
/// descends into, refuses an empty result, and holds a floor on what it found.
/// </summary>
public class BCVersionDefaultTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>
    /// Directory names the project walk never descends into.
    ///
    /// `.claude` is the load-bearing one and the reason #3139 exists — agent worktrees under
    /// `.claude/worktrees/` are full checkouts of this repository, so walking them makes this
    /// guard's answer depend on other branches. `al-language` is the read-only upstream
    /// submodule, skipped by NAME rather than by a `tests/al-language/` path prefix so that it
    /// is still skipped when it appears at another depth (a worktree's own copy sits at
    /// `.claude/worktrees/&lt;id&gt;/tests/al-language/`, which the old prefix test never matched).
    /// The build-output and tooling names are ordinary hygiene.
    /// </summary>
    private static readonly string[] SkippedDirectories =
    {
        "bin", "obj", ".git", ".claude", ".vs", "node_modules", "packages", "al-language",
    };

    /// <summary>
    /// Every MSBuild project file belonging to THIS repository, discovered by walking rather
    /// than by a maintained list, and never leaving the repository's own tree.
    /// Throws when the walk finds nothing: a scan with nothing to scan reports zero offenders
    /// and reads as success, which is the worst property a guard can have (the #3021 /
    /// #3092 non-vacuity rule).
    /// </summary>
    internal static IReadOnlyList<string> RepositoryProjectFiles(string root)
    {
        var found = EnumerateProjectFiles(root).ToList();

        if (found.Count == 0)
            throw new InvalidOperationException(
                $"Found no *.csproj under '{root}'. A scan with nothing to scan reports zero "
                + "offenders and reads as a pass, so it fails here instead (#3139).");

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// The walk itself, lazily, one directory at a time: the root's own project files first,
    /// then its subdirectories depth-first. Separate from <see cref="RepositoryProjectFiles"/>
    /// so a test can observe the walk WHILE it runs (#3206) rather than only its result.
    ///
    /// #3206: a SUBdirectory that disappears between being listed and being opened is skipped.
    /// This walk covers the repository root, and other tests in this same assembly legitimately
    /// create and delete scratch directories there while it runs — <c>BuildDeterminismTests</c>
    /// needs its probe roots inside the repository tree so they inherit the real
    /// <c>Directory.Build.props</c>/<c>.targets</c> whose determinism is the thing under test.
    /// xUnit runs the two classes in parallel, and the loser was this one: PR #3180's BC 28.4
    /// leg failed here with a <c>DirectoryNotFoundException</c> naming a directory belonging to
    /// the other class, while an independent re-run of the same commit passed.
    ///
    /// The ROOT is deliberately NOT covered: a missing root still throws, because a mistyped or
    /// moved <c>RepoRoot</c> must be loud rather than quietly scanning nothing (#3139/#3021).
    /// Only <c>DirectoryNotFoundException</c> is caught — swallowing
    /// <c>UnauthorizedAccessException</c> or a general <c>IOException</c> would hide a
    /// permissions or filesystem regression behind the same silence.
    /// </summary>
    internal static IEnumerable<string> EnumerateProjectFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            string[] subs;
            string[] files;
            try
            {
                subs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir, "*.csproj");
            }
            catch (DirectoryNotFoundException) when (!string.Equals(dir, root, StringComparison.Ordinal))
            {
                continue; // deleted by a concurrently running test between the listing and here
            }

            foreach (var sub in subs)
            {
                if (SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.Ordinal)) continue;
                pending.Push(sub);
            }

            foreach (var file in files) yield return file;
        }
    }

    /// <summary>
    /// Live `&lt;_BCVersion` declarations in the project files under <paramref name="root"/>,
    /// as "relative/path: line". Whole-line comments do not count — a commented-out
    /// declaration is history, not a default.
    /// </summary>
    internal static IReadOnlyList<string> BCVersionDeclarations(string root)
    {
        var offenders = new List<string>();
        foreach (var file in RepositoryProjectFiles(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("<!--", StringComparison.Ordinal)) continue; // comment, not a live declaration
                if (Regex.IsMatch(trimmed, @"^<_BCVersion\b"))
                    offenders.Add($"{rel}: {line.Trim()}");
            }
        }
        return offenders;
    }

    [Fact]
    public void NoProjectFile_DeclaresItsOwnBCVersionDefault()
    {
        var projects = RepositoryProjectFiles(RepoRoot);

        // Anti-vacuity floor. RepositoryProjectFiles already refuses an empty walk; this
        // catches the subtler version where an added exclusion quietly narrows the scan to a
        // handful of files and the guard reports a clean tree it never looked at. The
        // repository tracks six .csproj files; naming two of them keeps the floor honest even
        // if the count changes.
        Assert.True(projects.Count >= 6,
            $"expected at least the repository's six tracked project files, found {projects.Count}: "
            + string.Join(", ", projects.Select(p => Path.GetRelativePath(RepoRoot, p))));
        var rels = projects.Select(p => Path.GetRelativePath(RepoRoot, p).Replace('\\', '/')).ToList();
        Assert.Contains("AlRunner/AlRunner.csproj", rels);
        Assert.Contains("AlRunner.Tests/AlRunner.Tests.csproj", rels);

        var offenders = BCVersionDeclarations(RepoRoot);
        Assert.True(offenders.Count == 0,
            "Only Directory.Build.props may declare a _BCVersion default. A per-project "
            + "default drifts from the shared one exactly like AlRunner.csproj and "
            + "AlRunner.Tests.csproj did (#2102): a bare local build silently picks up "
            + "whichever project MSBuild happens to evaluate first, and mismatched "
            + "defaults across projects break with CS1705. Offending declarations:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// #3139, the direct regression assertion: no path this guard reads may come from
    /// `.claude/`. On a developer or agent box that directory holds one full checkout of this
    /// repository per concurrent agent; on a CI runner it does not exist at all, because it is
    /// gitignored. A guard that reads it answers a different question in the two places.
    ///
    /// Read the limit of this one honestly: it only has anything to find when the suite runs
    /// from the MAIN checkout, because `RepoRoot` inside an agent worktree is the worktree,
    /// which has no nested `.claude/worktrees/` of its own — and CI has no `.claude/` at all.
    /// Measured on the main checkout: 126 project files before the fix, 6 after, matching
    /// `git ls-files '*.csproj'` exactly. The unconditional proof is the synthetic pair below,
    /// which builds the offending tree itself and therefore cannot go quiet depending on where
    /// it is run from.
    /// </summary>
    [Fact]
    public void ProjectWalk_NeverLeavesTheRepositoryIntoAgentWorktrees()
    {
        var strays = RepositoryProjectFiles(RepoRoot)
            .Select(p => Path.GetRelativePath(RepoRoot, p).Replace('\\', '/'))
            .Where(rel => rel.StartsWith(".claude/", StringComparison.Ordinal)
                       || rel.Contains("/tests/al-language/", StringComparison.Ordinal)
                       || rel.StartsWith("tests/al-language/", StringComparison.Ordinal))
            .ToList();

        Assert.True(strays.Count == 0,
            "This guard walked outside the repository's own tree. Everything under .claude/ is "
            + "another agent's checkout and everything under tests/al-language/ is a read-only "
            + "upstream submodule; neither is ours to police, and both make the answer depend "
            + "on local state that CI does not have (#3139). Strays:\n  "
            + string.Join("\n  ", strays));
    }

    /// <summary>
    /// The same claim proven against a synthetic tree, so it cannot pass merely because no
    /// worktree happens to declare `_BCVersion` today. That was exactly the state of the bug
    /// when it was found: latent, green, and one `git worktree add` away from failing in
    /// somebody else's checkout while naming a file that is not in their branch.
    /// </summary>
    [Fact]
    public void ProjectWalk_IgnoresADeclarationInsideAnAgentWorktree()
    {
        var root = TestScratch.Dir("bcver");
        try
        {
            WriteProject(root, "AlRunner/AlRunner.csproj", "<PropertyGroup></PropertyGroup>");
            WriteProject(root, ".claude/worktrees/other-agent/AlRunner/AlRunner.csproj",
                "<PropertyGroup>\n    <_BCVersion>28.1.49838.53910</_BCVersion>\n  </PropertyGroup>");
            WriteProject(root, "tests/al-language/Sub/Sub.csproj",
                "<PropertyGroup>\n    <_BCVersion>27.0.0.0</_BCVersion>\n  </PropertyGroup>");

            var found = RepositoryProjectFiles(root)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .ToList();
            Assert.Equal(new[] { "AlRunner/AlRunner.csproj" }, found);

            Assert.Empty(BCVersionDeclarations(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The negative direction, and the one that keeps the exclusions from neutering the guard:
    /// a declaration in the repository's OWN project files is still caught, and the message
    /// names the file and the line so the author knows what to delete.
    /// </summary>
    [Fact]
    public void ProjectWalk_StillCatchesADeclarationInTheRepositorysOwnProject()
    {
        var root = TestScratch.Dir("bcver");
        try
        {
            WriteProject(root, "AlRunner/AlRunner.csproj",
                "<PropertyGroup>\n    <_BCVersion>28.1.49838.53910</_BCVersion>\n  </PropertyGroup>");
            WriteProject(root, "tools/DownloadArtifacts/DownloadArtifacts.csproj",
                "<PropertyGroup></PropertyGroup>");

            var offenders = BCVersionDeclarations(root);
            Assert.Single(offenders);
            Assert.StartsWith("AlRunner/AlRunner.csproj: ", offenders[0]);
            Assert.Contains("<_BCVersion>28.1.49838.53910</_BCVersion>", offenders[0]);

            // A commented-out declaration is history, not a default.
            WriteProject(root, "AlRunner.Tests/AlRunner.Tests.csproj",
                "<PropertyGroup>\n    <!-- <_BCVersion>27.0.0.0</_BCVersion> -->\n  </PropertyGroup>");
            Assert.Single(BCVersionDeclarations(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A walk that finds nothing must throw, not report a clean tree. A moved or mistyped root
    /// is the one failure that turns this whole class into decoration.
    /// </summary>
    [Fact]
    public void ProjectWalk_ThrowsWhenItFindsNoProjectsAtAll()
    {
        var root = TestScratch.Dir("bcver");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude", "worktrees", "other-agent"));
            File.WriteAllText(Path.Combine(root, ".claude", "worktrees", "other-agent", "X.csproj"), "<Project />");

            var ex = Assert.Throws<InvalidOperationException>(() => RepositoryProjectFiles(root));
            Assert.Contains("reads as a pass", ex.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// #3206: a directory that vanishes between being listed and being opened must be skipped,
    /// not fail the walk.
    ///
    /// This walk is over the repository root, and other tests in this same assembly write
    /// scratch directories there and delete them again — <c>BuildDeterminismTests</c> creates
    /// <c>&lt;RepoRoot&gt;/.build-determinism-probe-*</c> and two
    /// <c>.build-determinism-path-*</c> roots, which have to live inside the repository tree to
    /// inherit the real <c>Directory.Build.props</c>/<c>.targets</c> the determinism claim is
    /// about. xUnit runs the two classes in parallel, so the entry can be gone by the time this
    /// walk opens it. Measured on PR #3180's BC 28.4 leg (run 34046877973): this class failed
    /// with <c>DirectoryNotFoundException</c> naming a directory belonging to the OTHER class,
    /// while an independent second run of the same commit passed.
    ///
    /// Note this is NOT what <c>SearchOption.AllDirectories</c> does — that recursion opens
    /// discovered subdirectories with "ignore not found" set, which is why the pre-#3139
    /// version of this scan never hit it and the manual walk does.
    ///
    /// Deletes the victims after the FIRST yielded path, which is deterministic: the walk pops
    /// the root first and yields the root's own project files before descending, so every
    /// victim is still unvisited at that point. <c>keep/</c> proves the walk still descends
    /// afterwards rather than stopping at the first casualty.
    /// </summary>
    [Fact]
    public void ProjectWalk_SkipsADirectoryDeletedWhileTheWalkIsRunning()
    {
        var root = TestScratch.Dir("bcver");
        try
        {
            WriteProject(root, "Root.csproj", "<PropertyGroup></PropertyGroup>");
            WriteProject(root, "keep/Keep.csproj", "<PropertyGroup></PropertyGroup>");
            var victims = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                WriteProject(root, $".build-determinism-path-{i}/Victim.csproj", "<PropertyGroup></PropertyGroup>");
                victims.Add(Path.Combine(root, $".build-determinism-path-{i}"));
            }

            var seen = new List<string>();
            var deleted = false;
            foreach (var path in EnumerateProjectFiles(root))
            {
                seen.Add(Path.GetRelativePath(root, path).Replace('\\', '/'));
                if (deleted) continue;
                foreach (var victim in victims) Directory.Delete(victim, recursive: true);
                deleted = true;
            }

            seen.Sort(StringComparer.Ordinal);
            Assert.Equal(new[] { "Root.csproj", "keep/Keep.csproj" }, seen);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The negative direction, and the reason the skip is scoped to SUBdirectories: a ROOT that
    /// does not exist must still throw. Swallowing that would turn a mistyped or moved
    /// <c>RepoRoot</c> into a walk that finds nothing and — with the non-vacuity guard gone or
    /// weakened — reads as a clean tree, which is the #3139/#3021 failure this class already
    /// refuses.
    /// </summary>
    [Fact]
    public void ProjectWalk_StillThrowsWhenTheRootItselfIsMissing()
    {
        var root = Path.Combine(TestScratch.Dir("bcver"), "no-such-directory");

        Assert.Throws<DirectoryNotFoundException>(() => EnumerateProjectFiles(root).ToList());
        Assert.Throws<DirectoryNotFoundException>(() => RepositoryProjectFiles(root));
    }

    private static void WriteProject(string root, string relativePath, string propertyGroup)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  {propertyGroup}\n</Project>\n");
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
