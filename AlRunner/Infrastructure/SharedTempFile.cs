// SharedTempFile — publish a file into a SHARED, content-addressed temp path so a concurrent
// reader never sees it half-written (#2967).
//
// The shape this exists to remove
// -------------------------------
//     var path = Path.Combine(Path.GetTempPath(), $"al-runner-thing-{contentKey}");
//     if (!File.Exists(path))
//     {
//         using var fs = File.Create(path);      // the path becomes visible HERE, at 0 bytes
//         source.CopyTo(fs);                     // and stays short for the whole copy
//     }
//     Use(path);
//
// `File.Create` publishes the name before a single byte of content, so the window between it
// and the last write is a window in which any other process on the machine passes its own
// `File.Exists` check, skips the write, and uses a truncated file. The name is derived from
// the content, so it is stable across processes by design — which is exactly what makes the
// race reachable rather than theoretical, and this box runs nine runners at once.
//
// For a BC .app the truncated read surfaces as `AL1023: The package file ... is not valid`,
// attributed to the compilation rather than to the file, so it fails a whole run.
//
// The fix, and why a rename is enough
// -----------------------------------
// Write to a private `<path>.tmp-<rand>` in the same directory and rename it onto the final
// name. Same directory means same filesystem, so the rename is one `rename(2)` — measured
// under strace for the directory form of the same operation in PkgDedupStaging, and atomic by
// POSIX. A reader sees the name either absent or complete, never short, with no lock between
// processes.
//
// That rename is ALREADY implemented, by AlCacheWriter.AtomicPublish (#1810, for the same
// truncated-read defect one layer up), so this delegates to it rather than growing a second
// copy of the mechanism. What this adds on top is the policy the shared content-addressed
// sites need and a cache publish does not: skip the work entirely when a usable file is
// already there, and treat a zero-length file as NOT usable so a leftover from a build that
// predates this — or from a process killed between File.Create and its first write — is
// replaced rather than adopted forever.
namespace AlRunner.Infrastructure;

internal static class SharedTempFile
{
    /// <summary>
    /// Ensure <paramref name="path"/> exists and holds the full content produced by
    /// <paramref name="write"/>, without ever exposing a partially written file under that
    /// name. Returns <paramref name="path"/>.
    /// </summary>
    /// <param name="isUsable">
    /// Decides whether a file already at <paramref name="path"/> may be adopted as-is.
    /// Defaults to "non-zero length". A caller that knows the expected size should say so.
    /// </param>
    internal static string PublishAtomically(string path, Action<Stream> write,
                                             Func<FileInfo, bool>? isUsable = null)
    {
        isUsable ??= static fi => fi.Length > 0;

        var existing = new FileInfo(path);
        if (existing.Exists && isUsable(existing)) return path;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // overwrite: true, i.e. last-writer-wins, is correct here for the same reason
        // AlCacheWriter gives — the name is a content address, so two concurrent writers
        // produce the same bytes and either rename leaves the right file in place.
        AlCacheWriter.AtomicPublish(path, tmp =>
        {
            using var fs = File.Create(tmp);
            write(fs);
        });
        return path;
    }
}
