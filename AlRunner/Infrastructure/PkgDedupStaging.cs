// PkgDedupStaging — publishes BcCompiler's package-dedup staging directory from its
// per-run `<key>.tmp-<rand>` scratch name to the shared, content-addressed `<key>` name.
//
// Split out of BcCompiler.DeduplicateAppPackageDirs so the publish step can be unit
// tested on its own: BcCompiler drags the whole BC service-tier reference chain in with
// it, while this is plain filesystem logic.
//
// Why it is not a bare Directory.Move (#1691): on Windows the move of a directory whose
// files were written moments earlier intermittently fails with
//
//     IOException: Access to the path 'C:\...\Temp\al-runner-pkgdedup\<key>.tmp-<rand>' is denied.
//
// consistent with an AV scanner or the search indexer briefly holding a handle inside the
// freshly-written tree. It is transient — an immediate re-run of the same command always
// passes — but the old code rethrew, which surfaced as `EMIT-FAIL — IOException` and killed
// the whole run (reported at ~1 in 5 runs on a non-admin Windows box, where symlink creation
// is unavailable so every staged entry is a full file copy and the write volume is highest).
//
// Two changes make that non-fatal, in order of preference:
//   1. Retry the move a few times with a short backoff, so the common transient handle
//      clears and the shared, reusable `<key>` dir still gets published.
//   2. If it still will not move, fall back to using the scratch directory itself. Its
//      contents are exactly what the move would have published — same files, staged by the
//      same code path — so the compile that consumes it behaves identically. All that is
//      lost is cross-compile reuse of this one key, which is a cache miss, not a wrong
//      answer. That is strictly better than discarding a run that is already half done.
using System.Diagnostics;

namespace AlRunner.Infrastructure;

internal static class PkgDedupStaging
{
    internal const int DefaultAttempts = 5;

    /// <summary>
    /// Move <paramref name="tmp"/> onto <paramref name="stage"/> and return the directory
    /// callers should scan. That is <paramref name="stage"/> whenever it can be published or
    /// already exists, and <paramref name="tmp"/> when it cannot — never an exception, and
    /// never a directory that is missing the staged packages.
    /// </summary>
    /// <param name="warn">Where the fallback notice goes; null suppresses it.</param>
    /// <param name="attempts">Total move attempts, including the first.</param>
    /// <param name="backoff">
    /// Called with the 1-based attempt number after each failed attempt except the last.
    /// Tests inject it to drive the retry deterministically; production uses a short sleep.
    /// </param>
    internal static string Publish(string tmp, string stage, TextWriter? warn = null,
                                   int attempts = DefaultAttempts, Action<int>? backoff = null)
    {
        Debug.Assert(attempts >= 1);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Directory.Move(tmp, stage);
                return stage;
            }
            catch (Exception ex)
            {
                // Another compile — in this process or a concurrent al-runner — published the
                // same key first. The key is a hash of the picked .app set, so their directory
                // is ours by construction: adopt it and drop the duplicate.
                if (Directory.Exists(stage))
                {
                    TryDelete(tmp);
                    return stage;
                }
                if (attempt == attempts)
                {
                    warn?.WriteLine(
                        $"  [pkgdedup] could not publish staging dir to '{stage}' after " +
                        $"{attempts} attempt(s) ({ex.GetType().Name}: {ex.Message}) — " +
                        $"using '{tmp}' for this run");
                    return tmp;
                }
            }
            // Between attempts only. The caller holds BcCompiler's staging lock here, so this
            // is deliberately bounded and short (150ms total at the default 5 attempts) — the
            // handle this waits on is held by a scanner for milliseconds, and the alternative
            // being traded against is losing the run outright.
            if (backoff != null) backoff(attempt);
            else Thread.Sleep(10 * attempt);
        }
        return tmp; // not reachable: the attempts==attempt branch above always returns
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
