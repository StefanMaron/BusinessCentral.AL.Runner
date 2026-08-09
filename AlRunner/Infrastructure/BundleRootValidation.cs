// BundleRootValidation — up-front validation of the positional bundle paths (#1713).
//
// Before this, a positional path that did not exist travelled all the way to
// EnumerateSuites/EnumerateSuitesBelow, where Directory.EnumerateDirectories threw a
// raw System.IO.DirectoryNotFoundException out of Main: a .NET stack trace and exit
// code 134 — the code the CI matrix documents as "crash" — for the single most
// ordinary user error there is, and only AFTER ~6s of BC patch application.
//
// The runner is otherwise disciplined about loud, named failures (BcArtifacts names
// the exact download command; ProvisioningCheck prints the command to run). This is
// the same rule applied to argument handling: name the path that failed, show what
// part of it does exist, and — when the evidence actually supports it — name the fix.

namespace AlRunner.Infrastructure;

/// <summary>
/// Validates the positional bundle directories handed to the CLI. Called at
/// argument-parse time, before the BC artifact selection, the Cecil re-exec and the
/// patch pass, so a mistyped path costs milliseconds instead of seconds.
/// </summary>
internal static class BundleRootValidation
{
    /// <summary>
    /// Returns <c>null</c> when every root names an existing directory; otherwise the
    /// complete multi-line failure message for the FIRST unusable root, ready to write
    /// to stderr. The caller exits 2 ("a bundle could not execute", the ladder every
    /// other CLI usage error in Program.cs already uses — see PrintHelp).
    /// </summary>
    public static string? Validate(IReadOnlyList<string> bundleRoots)
    {
        foreach (var root in bundleRoots)
        {
            var problem = Describe(root);
            if (problem != null) return problem;
        }
        return null;
    }

    /// <summary>
    /// The message for one root, or <c>null</c> when it is a usable directory.
    /// Split out from <see cref="Validate"/> so the "first bad root wins" policy and
    /// the per-root diagnosis are independently testable.
    /// </summary>
    public static string? Describe(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;   // nothing to diagnose

        string abs;
        try { abs = Path.GetFullPath(root); }
        catch (Exception ex)
        {
            // An unparseable path (invalid characters, a path longer than the platform
            // allows) is still a user error, not a crash: name it the same way.
            return $"al-runner: not a usable path: {root}" + Environment.NewLine
                 + $"  {ex.GetType().Name}: {ex.Message}" + Environment.NewLine
                 + PositionalArgumentReminder;
        }

        if (Directory.Exists(abs)) return null;              // the ONLY success path

        var lines = new List<string>();
        if (File.Exists(abs))
        {
            // Distinct from "no such directory": the path is there, it is just the wrong
            // kind of thing (pointing at app.json instead of the folder holding it is the
            // usual way to land here). Saying "no such directory" would send the reader
            // looking for a typo that isn't there.
            lines.Add($"al-runner: not a directory: {root}");
            if (!PathsEqual(root, abs)) lines.Add($"  resolved to: {abs}");
        }
        else
        {
            lines.Add($"al-runner: no such directory: {root}");
            if (!PathsEqual(root, abs)) lines.Add($"  resolved to:             {abs}");
            var parent = DeepestExistingParent(abs);
            if (parent != null)
                lines.Add($"  deepest existing parent: {parent}");
        }

        lines.Add(PositionalArgumentReminder);

        var hint = UninitialisedSubmoduleHint(abs);
        if (hint != null) lines.Add(hint);

        return string.Join(Environment.NewLine, lines);
    }

    private const string PositionalArgumentReminder =
        "  Positional arguments are bundle directories (each holds app.json and/or .al files).";

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.Ordinal);

    /// <summary>
    /// The nearest ancestor of <paramref name="abs"/> that does exist. This is what makes
    /// the doubled-prefix typo from #1713 (`tests/al-language/tests/al-language`) obvious
    /// at a glance: the parent stops exactly where the duplication begins.
    /// </summary>
    private static string? DeepestExistingParent(string abs)
    {
        try
        {
            var dir = Directory.GetParent(abs);
            while (dir != null)
            {
                if (dir.Exists) return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch { /* an unreadable ancestor simply yields no extra context */ }
        return null;
    }

    /// <summary>
    /// A hint naming `git submodule update --init --recursive` — but ONLY when the
    /// evidence supports it: a `.gitmodules` in some ancestor declares a submodule whose
    /// path contains the requested one, AND that submodule directory is missing or empty
    /// (what `git clone` without `--recurse-submodules` leaves behind).
    ///
    /// Deliberately silent when the submodule IS checked out — that is the #1713 repro
    /// itself, where the path is simply mistyped and the hint would be a wrong lead.
    /// </summary>
    private static string? UninitialisedSubmoduleHint(string abs)
    {
        try
        {
            var dir = Directory.GetParent(abs);
            while (dir != null)
            {
                var gitmodules = Path.Combine(dir.FullName, ".gitmodules");
                if (File.Exists(gitmodules))
                {
                    foreach (var declared in ReadSubmodulePaths(gitmodules))
                    {
                        var subAbs = Path.GetFullPath(Path.Combine(dir.FullName, declared));
                        if (!IsAtOrUnder(abs, subAbs)) continue;
                        if (Directory.Exists(subAbs)
                            && Directory.EnumerateFileSystemEntries(subAbs).Any())
                            continue;   // checked out — the path is just wrong
                        return $"  {subAbs} is a git submodule and is not checked out — run:"
                             + Environment.NewLine
                             + "    git submodule update --init --recursive";
                    }
                    return null;   // nearest .gitmodules decides; do not keep climbing
                }
                dir = dir.Parent;
            }
        }
        catch { /* a hint is a nicety; never let it become the failure */ }
        return null;
    }

    /// <summary>Reads the `path = …` values out of a .gitmodules file.</summary>
    private static IEnumerable<string> ReadSubmodulePaths(string gitmodules)
    {
        foreach (var raw in File.ReadLines(gitmodules))
        {
            var line = raw.Trim();
            if (!line.StartsWith("path", StringComparison.Ordinal)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            // Guard against a key such as "pathspec" that merely starts with "path".
            if (line[..eq].Trim() != "path") continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length > 0) yield return value;
        }
    }

    private static bool IsAtOrUnder(string candidate, string ancestor)
    {
        var a = ancestor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var c = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(a, c, StringComparison.Ordinal)
            || c.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
