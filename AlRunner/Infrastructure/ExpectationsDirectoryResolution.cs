// ExpectationsDirectoryResolution — locates the tests/expectations/ manifest
// directory for a run when the caller has NOT passed --expectations explicitly.
//
// Issue #1984: the original auto-probe checked ONLY Environment.CurrentDirectory
// (see the comment that used to sit at Program.cs's expectations block). That meant
//
//   cwd = repo root                          → tests/expectations FOUND
//   cwd = AlRunner/bin/Release/net8.0         → tests/expectations MISSED
//
// for the byte-identical `al-runner ... tests/al-language/tests/al-language`
// invocation — the manifest's relevance depends on which bundle is being run, not on
// where the shell happens to be sitting. Worse, the miss was completely silent: an
// explicit --expectations pointing at a missing directory exits 2 loudly, but the
// auto-probed default just left `expectations` null and every expect-oos /
// expect-divergence test in the run silently flipped from its classified outcome to
// a plain FAIL.
//
// Fix: resolve relative to the BUNDLE path too. A bundle argument names where the
// suite lives; walking UP from it to find a `tests/expectations` sibling is the same
// thing you'd do by eye if asked "which repo's manifest applies to this bundle?".
// cwd stays a secondary probe (kept for back-compat: a relative bundle path invoked
// from the repo root already worked before this fix, and should keep working).
using System;
using System.Collections.Generic;
using System.IO;

namespace AlRunner.Infrastructure;

/// <summary>
/// Pure, testable resolution logic for the auto-probed <c>tests/expectations</c>
/// manifest directory. See file header for the bug this exists to fix (#1984).
/// </summary>
public static class ExpectationsDirectoryResolution
{
    /// <summary>
    /// Probes for <c>&lt;ancestor&gt;/tests/expectations</c>, walking UP from each
    /// bundle root's absolute path first (in the order given), then from
    /// <paramref name="currentDirectory"/>. Returns the first existing directory
    /// found, or null if none of the probed locations exist.
    /// </summary>
    public static string? Resolve(IReadOnlyList<string> bundleRoots, string currentDirectory)
    {
        foreach (var root in bundleRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string start;
            try { start = Path.GetFullPath(root); }
            catch { continue; }   // an unusable bundle path is diagnosed elsewhere (BundleRootValidation)
            var found = TryWalkUp(start);
            if (found != null) return found;
        }

        string cwdStart;
        try { cwdStart = Path.GetFullPath(currentDirectory); }
        catch { return null; }
        return TryWalkUp(cwdStart);
    }

    /// <summary>
    /// Walks from <paramref name="start"/> (a directory, or a file/nonexistent path
    /// whose containing directory is used instead) up through every ancestor,
    /// looking for a <c>tests/expectations</c> subdirectory at each level. Stops at
    /// the filesystem root.
    /// </summary>
    private static string? TryWalkUp(string start)
    {
        var dir = Directory.Exists(start) ? start : Path.GetDirectoryName(start);
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tests", "expectations");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;   // reached the filesystem root
            dir = parent;
        }
        return null;
    }
}
