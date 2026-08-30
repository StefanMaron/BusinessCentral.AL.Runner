// PrebuiltShadowCheck — decides whether a prebuilt .app found in the package cache is still
// a valid stand-in for an impl bundle's SOURCE.
//
// The layered pre-pass prefers a real, alc-built .app over in-process symbol synthesis,
// because BC's native .app scanner merges tableextensions correctly where our synthetic
// symbols.json does not (see RunLayeredPrePass). That preference is correct — but it was
// unconditional, matched on AppId alone. A stale .app in a project's .alpackages therefore
// shadowed the source directory the user passed on the command line, and the failure
// surfaced as a wall of AL0791 / AL0185 diagnostics against source that is perfectly valid.
//
// Observed on Pageworks: a months-old Stefan Maron Consulting_Pageworks_1.0.0.0.app produced
// 136 bogus "namespace 'Copilot' is unknown" / "Codeunit '...' is missing" errors, because the
// cached package predates that source. Deleting the .app made the same run compile and execute
// 1076 tests.

namespace AlRunner.Infrastructure;

internal static class PrebuiltShadowCheck
{
    /// <summary>
    /// Newest last-write time (UTC) across every <c>*.al</c> file under <paramref name="bundleDir"/>,
    /// recursively. <see cref="DateTime.MinValue"/> when the directory is missing, unreadable, or
    /// contains no AL source — in which case nothing can be newer than a prebuilt, so the prebuilt
    /// keeps winning.
    ///
    /// Only <c>*.al</c> counts: a build artifact, log, or editor swapfile dropped into the bundle
    /// must not read as "the source changed".
    /// </summary>
    public static DateTime NewestAlSourceUtc(string bundleDir)
    {
        try
        {
            if (!Directory.Exists(bundleDir)) return DateTime.MinValue;

            var newest = DateTime.MinValue;
            foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(bundleDir, "*.al"))
            {
                var t = File.GetLastWriteTimeUtc(file);
                if (t > newest) newest = t;
            }
            return newest;
        }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>
    /// Whether the bundle's source has changed since the prebuilt <c>.app</c> was written, i.e.
    /// the prebuilt is stale and must NOT be allowed to shadow the source.
    ///
    /// Equal timestamps keep the prebuilt: a freshly built .app and its sources routinely share
    /// a timestamp, and that is the normal "prebuilt is current" case.
    /// </summary>
    public static bool SourceIsNewer(DateTime prebuiltUtc, DateTime newestSourceUtc)
        => newestSourceUtc > prebuiltUtc;
}
