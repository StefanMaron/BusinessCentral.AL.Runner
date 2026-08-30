// BundleRootDeduplication — collapses positional bundle arguments that name the SAME
// directory on disk, so each bundle runs once per invocation (#2136).
//
// The bug: `al-runner ./x ./x` ran the bundle twice, so a 1-test bundle reported 2
// tests. Nothing crashed — that was #1692, where SerializeJsonOutput keyed a dictionary
// on the bundle's last path segment and threw ArgumentException AFTER the tests had run;
// it now keys on the full path. What was left behind is the quieter half of the same
// mistake: the run itself never asked whether two arguments were the same directory, so
// it silently doubled the test count. The count is what CI gates on and what
// tests/expectations/count-baseline/ pins, which makes a silently doubled count worse
// than a crash.
//
// WHY DEDUPLICATE AT ALL (rather than documenting "duplicates run twice"): a repeated
// path on a command line is essentially always an accident — an overlapping shell glob,
// a script appending a default plus an explicit argument. Running a suite twice on
// purpose (to shake out order dependence) is a real thing to want, but nobody expresses
// it by typing the same directory twice, and it deserves its own flag rather than being
// inferred from a duplicate argument.
//
// WHAT COUNTS AS "THE SAME DIRECTORY" — the resolved real path, NOT the argument string:
//   * absolute, with '.' / '..' collapsed  (Path.GetFullPath)
//   * trailing separators trimmed          ('x' == 'x/')
//   * symlinks followed, in EVERY path component, not just the last one
// De-duplicating raw strings would fix only the identical-spelling case and leave `x`
// vs `./x` vs `/abs/x` doubling exactly as before — the same "compared the convenient
// string instead of the actual path" shape as #1692's basename key.
//
// WHAT IS DELIBERATELY *NOT* THE KEY: the bundle's app identity. Two DIFFERENT
// directories that declare the same app id and version are not duplicates — the user
// really did name two directories — and the runner already has loud, deliberate
// handling for that case: the #1683 module reuse prints
// "AppId … already loaded earlier in this process — reusing that module instead of
// recompiling" and runs the second bundle against the first one's module. Collapsing
// them here would silently discard an argument that was typed on purpose, which is the
// failure mode .claude/rules/loud-failures.md exists to prevent.
//
// The dedup is NOT silent either: the caller prints DescribeDropped's notice naming both
// spellings and the path they share.

namespace AlRunner.Infrastructure;

/// <summary>
/// De-duplicates the positional bundle directories handed to the CLI (and the
/// <c>sourcePaths</c> of a server request) by resolved real path. Runs AFTER
/// <see cref="BundleRootValidation"/>, so by this point every root is known to exist —
/// a mistyped path must still be reported as a mistyped path, never quietly folded into
/// a similar-looking sibling.
/// </summary>
internal static class BundleRootDeduplication
{
    /// <summary>One argument that was dropped, and the earlier argument it duplicates.</summary>
    public sealed record DroppedRoot(string Argument, string KeptArgument, string ResolvedPath);

    /// <summary>
    /// <paramref name="Roots"/> holds the surviving arguments in their ORIGINAL spelling
    /// and original order (the first spelling of a directory wins); <paramref name="Dropped"/>
    /// holds one entry per argument removed.
    /// </summary>
    public sealed record Result(IReadOnlyList<string> Roots, IReadOnlyList<DroppedRoot> Dropped);

    /// <summary>
    /// Path comparison matching the filesystem's own: ordinal on Linux (where two names
    /// differing only in case are two different directories), case-insensitive elsewhere.
    /// Folding case on Linux would collapse genuinely distinct bundles.
    /// </summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static Result Deduplicate(IReadOnlyList<string> bundleRoots)
    {
        var kept = new List<string>(bundleRoots.Count);
        var dropped = new List<DroppedRoot>();
        var seen = new Dictionary<string, string>(PathComparer);   // canonical path -> kept argument

        foreach (var root in bundleRoots)
        {
            // An empty/whitespace argument is not a path and has no "same directory"
            // question to answer; BundleRootValidation ignores it too. Pass it through
            // untouched rather than inventing a canonical form for it.
            if (string.IsNullOrWhiteSpace(root)) { kept.Add(root); continue; }

            var canonical = Canonicalize(root);
            if (seen.TryGetValue(canonical, out var keptArgument))
            {
                dropped.Add(new DroppedRoot(root, keptArgument, canonical));
                continue;
            }

            seen[canonical] = root;
            kept.Add(root);
        }

        return new Result(kept, dropped);
    }

    /// <summary>
    /// The resolved real path of <paramref name="root"/>: absolute, '.'/'..' collapsed,
    /// trailing separators trimmed, and every symlinked component followed to its final
    /// target. Returns the input unchanged when it cannot be resolved (an unparseable
    /// path, an unreadable ancestor) — canonicalisation must never become the failure,
    /// and two arguments that both fail to resolve simply compare as their own strings.
    /// </summary>
    public static string Canonicalize(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return root;

        string abs;
        try { abs = Path.GetFullPath(root); }
        catch { return root; }

        try { abs = ResolveSymlinkedComponents(abs); }
        catch { /* keep the lexical form; see the doc comment */ }

        return TrimTrailingSeparators(abs);
    }

    /// <summary>
    /// The stderr notice for a set of dropped arguments, or <c>null</c> when nothing was
    /// dropped. Names both spellings and the directory they share, so a caller who did
    /// NOT mean to pass the path twice can see which of their two arguments produced it.
    /// </summary>
    public static string? DescribeDropped(IReadOnlyList<DroppedRoot> dropped)
    {
        if (dropped.Count == 0) return null;

        var lines = new List<string>
        {
            $"al-runner: ignoring {dropped.Count} duplicate bundle argument"
                + (dropped.Count == 1 ? "" : "s")
                + " — each bundle directory runs once per invocation:",
        };
        foreach (var d in dropped)
        {
            lines.Add($"  '{d.Argument}' names the same directory as '{d.KeptArgument}'");
            lines.Add($"    both resolve to: {d.ResolvedPath}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Walks <paramref name="abs"/> component by component, replacing each symlinked
    /// component with its target. Resolving only the LAST component (what
    /// <c>Directory.ResolveLinkTarget</c> alone would give) misses the common shape where
    /// a parent directory is the link — `link-to-repo/tests/x` and `repo/tests/x`.
    /// </summary>
    private static string ResolveSymlinkedComponents(string abs)
    {
        var root = Path.GetPathRoot(abs);
        if (string.IsNullOrEmpty(root)) return abs;   // nothing to anchor a walk to

        var current = root;
        var rest = abs[root.Length..];
        foreach (var part in rest.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            current = ResolveOneLink(current);
        }
        return current;
    }

    // Deliberately reads LinkTarget (the raw stored target) rather than trusting
    // ResolveLinkTarget's returned FullName: a relative target such as `../sibling` must
    // be resolved against the LINK's own directory, and re-deriving it here makes that
    // explicit instead of depending on how the returned FileSystemInfo was rooted.
    private const int MaxSymlinkHops = 40;   // matches the usual kernel ELOOP budget

    private static string ResolveOneLink(string path)
    {
        var current = path;
        for (var hop = 0; hop < MaxSymlinkHops; hop++)
        {
            FileSystemInfo info;
            if (Directory.Exists(current)) info = new DirectoryInfo(current);
            else if (File.Exists(current)) info = new FileInfo(current);
            else return current;                       // does not exist — nothing to follow

            var target = info.LinkTarget;
            if (target == null) return current;        // not a link — done

            var parent = Path.GetDirectoryName(current);
            current = Path.GetFullPath(
                Path.IsPathRooted(target) || parent == null ? target : Path.Combine(parent, target));
        }
        // A symlink cycle. Returning the last hop keeps this a comparison key rather than
        // a failure: the run continues, and the two arguments are simply not merged.
        return current;
    }

    private static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Never trim away a filesystem root ("/" on Unix, "C:\" on Windows).
        return trimmed.Length == 0 ? path : trimmed;
    }
}
