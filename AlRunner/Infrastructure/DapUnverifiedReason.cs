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

        string full;
        try { full = Path.GetFullPath(sourcePath); }
        catch (ArgumentException) { return null; }   // a path shape this cannot reason about
        catch (NotSupportedException) { return null; }

        foreach (var failure in sourceMap.ScanFailures)
        {
            string failurePath;
            try { failurePath = Path.GetFullPath(failure.Path); }
            catch (ArgumentException) { continue; }
            catch (NotSupportedException) { continue; }

            if (DapBreakpointResolver.PathComparer.Equals(failurePath, full))
                return failure.Reason;

            // Under a directory or root that could not be scanned. The separator is appended
            // so `/src/app` does not swallow `/src/application`.
            var prefix = failurePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, PathComparison)) return failure.Reason;
        }
        return null;
    }

    /// <summary>The string comparison matching <see cref="DapBreakpointResolver.PathComparer"/> —
    /// case-sensitive only where the filesystem is.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
