// PerProcessScratch — a temp directory keyed on a readable name AND this process, for the
// sites where the old name-only key let two runners write over each other (#2967).
//
// This is the #2586 treatment, generalised. SiblingSymbolsDirectory fixed exactly one such
// path after two concurrent runners deleted each other's symbols mid-compile; the scan for
// #2967 found the same shape at two more:
//
//   * al-runner-query-symbols/<module>.SymbolReference.json — one file per MODULE NAME, opened
//     FileMode.Create (truncate) and, by its own comment, "overwritten each run so it tracks
//     the source". Module names are not unique across bundles on a machine — "tests",
//     "runner-extras", an app's own name — so two runners compiling same-named modules take
//     turns truncating one file, and the query column ids one of them reads back belong to
//     the other one's compile. That is a wrong answer, not a crash.
//   * al-runner-precompile/<publisher>_<name>_<version> — deletes every *.al in the directory,
//     writes this app's sources, then COMPILES OUT OF IT. Two --precompile runs of the same
//     app version (two worktrees at the same version string is the normal case here) race
//     between the delete and the compile: one compiles a partial source set, or the other's.
//
// Both are per-run working state that nothing outside the writing process ever reads, so
// sharing them bought nothing in the first place — unlike al-runner-pkgdedup and
// alrunner-v2-win32-stubs, which are shared caches on purpose and stay shared.
//
// The process component is also what makes a delete safe again: a process can only ever
// delete its own directory, which is what those calls always meant.
//
// Directories are registered with ScratchDirs, so a process that is KILLED before it can clean
// up has its directory reclaimed by the next runner start rather than left forever (#2706).
using System.Security.Cryptography;
using System.Text;

namespace AlRunner.Infrastructure;

internal static class PerProcessScratch
{
    /// <summary>
    /// One value per runner process. A GUID rather than the pid, for the reason
    /// <see cref="SiblingSymbolsDirectory"/> gives: pids are reused, and a reused pid collides
    /// with the directory of a previous owner that never got to clean up — the very failure
    /// this exists to prevent, arrived at from the other side.
    /// </summary>
    private static readonly string ProcessNonce = Guid.NewGuid().ToString("N");

    /// <summary>
    /// <c>&lt;temp&gt;/&lt;container&gt;/&lt;sanitized name&gt;-&lt;hash&gt;-&lt;process nonce&gt;</c>,
    /// created and owner-marked. A pure function of its arguments apart from the nonce, which is
    /// injectable so the properties that matter are testable without spawning processes: same
    /// (name, process) is always one directory, same name in two processes never is, and two
    /// different names never are.
    /// <para>The readable name stays in front of the hash so a human reading a temp listing can
    /// still tell what a directory is; the hash is what actually separates two names that
    /// sanitize to the same string.</para>
    /// </summary>
    internal static string Dir(string container, string name)
        => Dir(container, name, ProcessNonce);

    internal static string Dir(string container, string name, string processNonce)
    {
        var dir = Path.Combine(Path.GetTempPath(), container, Leaf(name, processNonce));
        return ScratchDirs.Create(dir);
    }

    /// <summary>The leaf name alone, with no filesystem side effect — what the tests assert on.</summary>
    internal static string Leaf(string name, string processNonce)
    {
        var safe = new string((name ?? string.Empty)
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_').ToArray());
        if (safe.Length == 0) safe = "unnamed";
        if (safe.Length > 60) safe = safe[..60];

        // Hash the ORIGINAL name, not the sanitized one: two different names that sanitize to
        // the same string ("a b" and "a/b") must still be two directories.
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(name ?? string.Empty)))[..12].ToLowerInvariant();

        return $"{safe}-{hash}-{processNonce}";
    }
}
