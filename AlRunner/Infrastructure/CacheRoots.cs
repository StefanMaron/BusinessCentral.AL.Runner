namespace AlRunner.Infrastructure;

/// <summary>
/// Process-global override for where the four caches that used to hardcode
/// <c>~/.cache/al-runner/&lt;name&gt;</c> actually write (issue #1821):
/// <c>compiled-deps</c> (<see cref="AlRunner.DependencyLoader"/>), <c>workspace-deps</c>
/// (<c>Program.cs</c>'s layered-workspace synthesis, two call sites), <c>ncl-cecil</c>
/// (<see cref="NclCecilRewrite"/>), and <c>bc-symbols</c>
/// (<see cref="AlRunner.Patches.BcAppSymbolCache"/>).
///
/// Only the AL-output cache (<c>Program.cs</c>'s <c>alCacheDir</c>, driven by
/// <c>--cache</c>/<c>--no-cache</c>) ever respected the <c>--cache</c> flag. The other
/// four always wrote to the real, shared, unscoped
/// <c>~/.cache/al-runner/&lt;name&gt;</c> regardless — so a caller that passes
/// <c>--cache &lt;isolated-dir&gt;</c> expecting per-invocation isolation (e.g. a test
/// using a fresh temp dir so each run starts from a clean slate) only actually got
/// isolation for AL output; the other four caches were silently shared, process-wide,
/// with every other <c>al-runner</c> invocation on the machine.
///
/// <para><b>Deliberately NOT wired into <c>alCacheDir</c> itself.</b> <c>alCacheDir</c>
/// keeps its pre-existing exact-directory semantics — writing straight into whatever
/// directory <c>--cache</c> names, no subfolder — because existing callers (tests,
/// <c>--watch</c>) already pass <c>&lt;root&gt;/al-out</c> as that value specifically for
/// AL-output isolation. This class instead resolves the OTHER four caches as named
/// subdirectories of that same <c>--cache</c> value (<c>&lt;dir&gt;/compiled-deps</c>,
/// etc.), so passing <c>--cache &lt;dir&gt;</c> isolates all five caches under one root
/// without changing what <c>al-out</c> alone has always done with that same value.</para>
///
/// <para>Set once at startup from the same value Program.cs assigns to <c>alCacheDir</c>
/// on a <c>--cache &lt;dir&gt;</c> flag (not on <c>--no-cache</c> — that flag has never
/// touched these four caches and doesn't start now; only <c>--cache</c> is in scope for
/// this issue). No flag at all (or <c>--no-cache</c>, or a bare-default run) means
/// <see cref="Resolve"/> falls back to exactly the same
/// <c>~/.cache/al-runner/&lt;name&gt;</c> path every one of these four caches already
/// used before this issue was fixed — the default behaviour, including CI's own
/// caching (e.g. the <c>smoke</c> job's <c>rm -rf ~/.cache/al-runner/ncl-cecil/</c>,
/// which never passes <c>--cache</c>), is unchanged.</para>
/// </summary>
public static class CacheRoots
{
    private static string? _override;

    /// <summary>
    /// Sets the process-global cache-root override for this run. Pass the exact value
    /// Program.cs's <c>--cache &lt;dir&gt;</c> parsing assigned to <c>alCacheDir</c>, or
    /// <c>null</c> when <c>--cache</c> was not given (including <c>--no-cache</c> runs —
    /// see class remarks). Idempotent to call more than once; the last call wins, mirroring
    /// how <c>alCacheDir</c> itself is just a plain mutable local reassigned by whichever
    /// <c>--cache</c>/<c>--no-cache</c> argument appears last on the command line.
    /// </summary>
    public static void SetOverride(string? cacheDir) => _override = cacheDir;

    /// <summary>
    /// Resolves the on-disk directory for the named cache (e.g. <c>"compiled-deps"</c>).
    /// Returns <c>&lt;override&gt;/&lt;name&gt;</c> when <see cref="SetOverride"/> was
    /// last called with a non-null directory; otherwise falls back to
    /// <c>~/.cache/al-runner/&lt;name&gt;</c>, the pre-#1821 hardcoded default every one
    /// of these four caches used unconditionally.
    /// </summary>
    public static string Resolve(string name)
    {
        var root = _override ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "al-runner");
        return Path.Combine(root, name);
    }

    /// <summary>Test-only: resets the override so test processes/hosts that share this
    /// static (e.g. in-process unit tests, as opposed to the spawned-subprocess
    /// integration tests that get natural per-process isolation) don't leak state
    /// between cases.</summary>
    internal static void ResetForTests() => _override = null;
}
