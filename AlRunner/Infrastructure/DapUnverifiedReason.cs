// DapUnverifiedReason — why a `setBreakpoints` line came back `verified: false`.
//
// There are three reasons and they call for different things from the client, so collapsing
// them loses the part that matters:
//
//   * the bundle did not compile          — nothing will ever bind; fix the AL
//   * that source could not be READ       — nobody knows whether the line has a statement
//   * that line carries no AL statement   — a measured fact about the AL
//
// The middle one is #3847: AlCoverageSourceMap.Build could not say "I could not read the
// sources", so a source it failed to open produced a map with no entry for that file's
// objects — and the adapter reported the third reason, a specific claim about AL nobody had
// read. `.claude/rules/guards-need-a-third-state.md`.
//
// Lifted out of RunDapLoop's handler so the discrimination is testable without a live DAP
// session: it is a decision over three states, and the handler is the wrong place to be
// reading one.
namespace AlRunner.Infrastructure;

public static class DapUnverifiedReason
{
    /// <summary>
    /// The message a DAP <c>Breakpoint.message</c> carries for an unverified line.
    /// </summary>
    /// <param name="compileFailure">The compile diagnostic, or null when the bundle compiled.</param>
    /// <param name="sourceMap">The map the request resolved against; its
    /// <see cref="AlSourceLocationMap.ScanFailures"/> is what makes the middle state
    /// visible.</param>
    /// <param name="sourcePath">The source file the client asked about.</param>
    public static string For(string? compileFailure, AlSourceLocationMap sourceMap, string sourcePath)
    {
        if (compileFailure != null)
            return $"the bundle did not compile, so nothing could be bound: {compileFailure}";

        if (UnreadableFor(sourceMap, sourcePath) is string unreadable)
            return $"this source could not be read, so whether that line carries a statement "
                 + $"is unknown rather than no: {unreadable}";

        return "no executable AL statement on this line in this file";
    }

    /// <summary>
    /// The scan failure covering <paramref name="sourcePath"/>, or null. A failure names the
    /// file itself, a directory the scan could not enter, or a root that is not there — so a
    /// containment test, not equality: a directory nobody could enter says nothing about the
    /// files under it, which is exactly the state being reported.
    /// </summary>
    private static string? UnreadableFor(AlSourceLocationMap sourceMap, string sourcePath)
    {
        if (sourceMap.ScanFailures.Count == 0) return null;

        if (Normalize(sourcePath) is not string full) return null;

        foreach (var failure in sourceMap.ScanFailures)
        {
            if (Normalize(failure.Path) is not string failurePath) continue;

            if (DapBreakpointResolver.PathComparer.Equals(failurePath, full))
                return failure.Reason;

            // Containment applies to a ROOT or a DIRECTORY, which cover the sources beneath
            // them, and never to a FILE, which covers exactly one path — a file failure was
            // being used as a prefix, so `C:\src\Locked.al` also claimed
            // `C:\src\Locked.al\Child.al` (#3884 review).
            if (failure.Kind == SourceScanFailureKind.File) continue;

            // The separator is appended so `/src/app` does not swallow `/src/application`.
            // Both sides are already separator-normalised by Normalize, so an extended-length
            // path whose caller wrote `\\?\C:\src/missing` still matches the sources under it.
            var prefix = failurePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, PathComparison)) return failure.Reason;
        }
        return null;
    }

    /// <summary>
    /// One spelling for both sides of every comparison, or null for a path shape this cannot
    /// reason about. GetFullPath alone is not enough: .NET deliberately leaves an
    /// extended-length path (<c>\\?\C:\...</c>) unnormalised, so a caller's mixed separators
    /// survive it and a prefix test then fails against a path that really is underneath
    /// (#3884 review). Separators are folded to the platform's own, and a trailing one is
    /// dropped so equality does not depend on it.
    /// </summary>
    private static string? Normalize(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }

        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            full = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        // Not the root itself: "C:\" and "/" must keep their separator.
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar);
        return trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':') ? full : trimmed;
    }

    /// <summary>The string comparison matching <see cref="DapBreakpointResolver.PathComparer"/> —
    /// case-sensitive only where the filesystem is.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
