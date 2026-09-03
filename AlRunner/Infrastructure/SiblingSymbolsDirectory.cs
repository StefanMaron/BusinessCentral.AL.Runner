// SiblingSymbolsDirectory — where EmitSiblingSymbols writes a bundle's in-bundle symbol files.
//
// WHY THIS EXISTS (issue #2586)
//   The path used to be Path.GetTempPath()/al-runner-sibling-symbols/<bundle leaf name>, and
//   EmitSiblingSymbols opens by deleting it recursively. Nothing in that path distinguishes one
//   bundle from another with the same leaf name, and nothing distinguishes one runner process
//   from another, so two failure modes followed:
//
//     1. Two runners racing. One creates the directory and starts writing *.symbols.json; the
//        other reaches the recursive delete and removes them mid-compile, and the first then
//        compiles a sibling against symbols that are no longer there. Leaf names collide easily
//        — "tests", "src", "app", "test-app" — and this machine runs the runner concurrently by
//        design: the C# suite spawns it around 130 times, CI runs four lanes, and several agent
//        worktrees run at once. Same class of defect as #2489 for the ncl-shadow root.
//     2. Two different bundles that both end in the same folder name resolving to one directory,
//        so one project's sibling symbols are visible to the other's compile.
//
//   Both come from the same missing information, so both are fixed by putting it in the path:
//   a hash of the bundle's full normalized path, and a per-process value.
//
//   The process component is what makes the recursive delete safe again. A process can only ever
//   delete its own directory, which is what that call always meant.
using System.Security.Cryptography;
using System.Text;

namespace AlRunner.Infrastructure;

internal static class SiblingSymbolsDirectory
{
    /// <summary>The parent holding every per-(bundle, process) directory. Also what
    /// <see cref="PruneStale"/> walks.</summary>
    internal static string Root => Path.Combine(Path.GetTempPath(), "al-runner-sibling-symbols");

    /// <summary>
    /// One value per runner process. A GUID rather than the PID: PIDs are reused, and a reused
    /// PID would collide with a directory the previous owner never got to clean up — which is
    /// the failure this exists to prevent, arrived at a different way.
    /// </summary>
    private static readonly string ProcessNonce = Guid.NewGuid().ToString("N");

    internal static string ForBundle(string bundlePath) => ForBundle(bundlePath, ProcessNonce);

    /// <summary>
    /// The directory for <paramref name="bundlePath"/> under <paramref name="processNonce"/>.
    /// A pure function of its two arguments, which is what makes the three properties that
    /// matter testable without spawning processes: the same bundle in the same process always
    /// gives the same path, the same bundle in two processes never does, and two different
    /// bundles never do — including when their leaf names are identical.
    /// <para>The leaf name stays in front of the hash so the directory is still recognisable to
    /// a human reading a temp listing; the hash is what actually separates bundles.</para>
    /// </summary>
    internal static string ForBundle(string bundlePath, string processNonce)
    {
        // Normalize BEFORE hashing so the same bundle reached by two spellings ("./tests" and an
        // absolute path, with or without a trailing separator) is one directory and not two.
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundlePath));
        // Windows paths are case-insensitive, so two spellings that differ only in case are the
        // same bundle there and must hash the same. On Linux and macOS they are different paths
        // and must not be folded together.
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12].ToLowerInvariant();

        var leaf = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(leaf)) leaf = "bundle";   // a filesystem root has no leaf name

        return Path.Combine(Root, $"{leaf}-{hash}-{processNonce}");
    }

    /// <summary>
    /// Delete directories under <see cref="Root"/> that have not been written to for
    /// <paramref name="maxAge"/>. Making each directory private to its process means nobody else
    /// cleans it up, so without this the temp root grows without bound.
    ///
    /// <para>The age threshold is deliberately far longer than any run: the whole point of the
    /// per-process component is that one process never deletes another's live directory, and a
    /// prune that guessed wrong would reintroduce exactly the bug being fixed. A directory
    /// untouched for a day belongs to a process that is not coming back.</para>
    ///
    /// <para>Every failure is swallowed. A concurrent prune from a sibling runner, a directory
    /// deleted between the listing and the delete, a permission error on a directory this user
    /// does not own — none of them is a reason to fail a test run, and the only cost of skipping
    /// one is that it is retried next time.</para>
    /// </summary>
    internal static void PruneStale(TimeSpan maxAge) => PruneStale(maxAge, DateTime.UtcNow);

    /// <summary>Injectable clock, so the age boundary is testable without waiting.</summary>
    internal static void PruneStale(TimeSpan maxAge, DateTime utcNow)
    {
        string[] entries;
        try
        {
            if (!Directory.Exists(Root)) return;
            entries = Directory.GetDirectories(Root);
        }
        catch (Exception) { return; }

        foreach (var entry in entries)
        {
            try
            {
                if (utcNow - Directory.GetLastWriteTimeUtc(entry) < maxAge) continue;
                Directory.Delete(entry, recursive: true);
            }
            catch (Exception) { /* another runner got there first, or it is not ours to delete */ }
        }
    }
}
