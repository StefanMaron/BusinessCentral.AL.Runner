namespace AlRunner.Infrastructure;

/// <summary>
/// Process-global override for where every cache resolved through <see cref="Resolve"/>
/// actually writes (issue #1821). At the time #1821 was fixed there were four such
/// caches: <c>compiled-deps</c> (<see cref="AlRunner.DependencyLoader"/>),
/// <c>workspace-deps</c> (<c>Program.cs</c>'s layered-workspace synthesis, two call
/// sites), <c>ncl-cecil</c> (<see cref="NclCecilRewrite"/>), and <c>bc-symbols</c>
/// (<see cref="AlRunner.Patches.BcAppSymbolCache"/>). More have been added since
/// (<c>ncl-shadow</c>, <c>app-manifests</c>, <c>r2r-chunks</c>, <c>install-baseline</c>)
/// — this class does not enumerate them because <see cref="Resolve"/> is the single
/// choke point every one of them already goes through; a new named cache gets this
/// class's behaviour for free just by calling <see cref="Resolve"/> instead of
/// hardcoding <c>~/.cache/al-runner/&lt;name&gt;</c> itself.
///
/// Originally only the AL-output cache (<c>Program.cs</c>'s <c>alCacheDir</c>, driven by
/// <c>--cache</c>/<c>--no-cache</c>) respected the <c>--cache</c> flag; every cache
/// resolved here wrote to the real, shared, unscoped <c>~/.cache/al-runner/&lt;name&gt;</c>
/// regardless — so a caller that passed <c>--cache &lt;isolated-dir&gt;</c> expecting
/// per-invocation isolation (e.g. a test using a fresh temp dir so each run starts from a
/// clean slate) only actually got isolation for AL output. #1821 fixed that for
/// <c>--cache</c>. #2555 does the equivalent for <c>--no-cache</c>: see
/// <see cref="DisableForRun"/>.
///
/// <para><b>Deliberately NOT wired into <c>alCacheDir</c> itself.</b> <c>alCacheDir</c>
/// keeps its pre-existing exact-directory semantics — writing straight into whatever
/// directory <c>--cache</c> names, no subfolder — because existing callers (tests,
/// <c>--watch</c>) already pass <c>&lt;root&gt;/al-out</c> as that value specifically for
/// AL-output isolation. This class instead resolves every OTHER cache as a named
/// subdirectory of that same <c>--cache</c> value (<c>&lt;dir&gt;/compiled-deps</c>,
/// etc.), so passing <c>--cache &lt;dir&gt;</c> isolates all of them under one root
/// without changing what <c>al-out</c> alone has always done with that same value. The
/// same split applies to <c>--no-cache</c>: it disables <c>alCacheDir</c> outright (no
/// disk cache consulted at all for AL output, not even a throwaway one — see
/// <c>Program.cs</c>'s own comment on why that is stronger than a redirect), while
/// <see cref="DisableForRun"/> redirects every cache resolved here to a fresh,
/// throwaway directory instead.</para>
///
/// <para>Set once at startup from the same value Program.cs assigns to <c>alCacheDir</c>
/// on a <c>--cache &lt;dir&gt;</c> flag, or via <see cref="DisableForRun"/> on
/// <c>--no-cache</c>. No flag at all (a bare-default run) means <see cref="Resolve"/>
/// falls back to exactly the same <c>~/.cache/al-runner/&lt;name&gt;</c> path every one
/// of these caches used before #1821 — the default behaviour, including CI's own caching
/// (e.g. the <c>smoke</c> job's <c>rm -rf ~/.cache/al-runner/ncl-cecil/</c>, which never
/// passes <c>--cache</c>), is unchanged.</para>
/// </summary>
public static class CacheRoots
{
    private static string? _override;
    private static string? _throwawayRoot;

    /// <summary>
    /// Environment variable that carries a <c>--no-cache</c> run's throwaway root across
    /// a re-exec (a fresh Cecil rewrite, or a shadow-dir hop for a missing Ncl.dll / a
    /// different BC-minor engine variant — both hand off to a child process mid-run).
    /// <see cref="DisableForRun"/> sets this in the CURRENT process's environment (not
    /// just on a specific child's <c>ProcessStartInfo</c>) the first time it mints a
    /// directory, so any child launched afterwards inherits it automatically and a call
    /// to <see cref="DisableForRun"/> in that child adopts the SAME directory instead of
    /// minting a second one. One throwaway root per RUN, not per PROCESS — the whole
    /// point of a throwaway root is that a key written earlier in the run (e.g.
    /// <c>ncl-cecil</c>, written once and then read by several app groups) is still a HIT
    /// later in the SAME run; a re-exec'd child that instead minted its own empty
    /// directory would treat that as a miss and redo the work the re-exec exists to avoid.
    /// </summary>
    public const string NoCacheRootEnvVar = "AL_RUNNER_NO_CACHE_ROOT";

    /// <summary>
    /// Sets the process-global cache-root override for this run. Pass the exact value
    /// Program.cs's <c>--cache &lt;dir&gt;</c> parsing assigned to <c>alCacheDir</c>, or
    /// <c>null</c> for the bare-default (no <c>--cache</c>, no <c>--no-cache</c>) case.
    /// Idempotent to call more than once; the last call wins, mirroring how
    /// <c>alCacheDir</c> itself is just a plain mutable local reassigned by whichever
    /// <c>--cache</c>/<c>--no-cache</c> argument appears last on the command line. Prefer
    /// <see cref="DisableForRun"/> for the <c>--no-cache</c> case — it also arranges
    /// cleanup and re-exec continuity that calling this directly with an ad hoc directory
    /// would not get.
    /// </summary>
    public static void SetOverride(string? cacheDir) => _override = cacheDir;

    /// <summary>
    /// <c>--no-cache</c> (#2555): redirects every cache resolved through <see cref="Resolve"/>
    /// to a throwaway per-run directory under the OS temp root, so a run reached for
    /// specifically to reproduce or measure a cold compile does not silently get these
    /// caches warm. Redirects rather than deletes anything under the real
    /// <c>~/.cache/al-runner</c> tree — erasing that tree would be a destructive side
    /// effect of a read-only-sounding flag, and would break any OTHER <c>al-runner</c>
    /// process sharing the machine (e.g. concurrent CI legs).
    ///
    /// Reuses the directory named by <see cref="NoCacheRootEnvVar"/> if that variable is
    /// already set in the environment (the re-exec-continuity case documented on that
    /// constant); otherwise mints a fresh one and publishes it there for any child this
    /// process goes on to launch. Calling this repeatedly within one process (e.g. both
    /// re-exec decision points in <c>Program.cs</c> run before any cache is actually
    /// touched) is safe and returns the same directory each time.
    /// </summary>
    public static string DisableForRun()
    {
        var existing = Environment.GetEnvironmentVariable(NoCacheRootEnvVar);
        string root;
        if (!string.IsNullOrEmpty(existing))
        {
            root = existing;
        }
        else
        {
            root = Path.Combine(Path.GetTempPath(), "al-runner-no-cache-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(NoCacheRootEnvVar, root);
        }
        _override = root;
        _throwawayRoot = root;
        return root;
    }

    /// <summary>
    /// Best-effort delete of the directory <see cref="DisableForRun"/> minted (a no-op if
    /// <see cref="DisableForRun"/> was never called, or the directory was never actually
    /// created by anything writing into it). Program.cs registers this on
    /// <c>AppDomain.ProcessExit</c> right after a <c>--no-cache</c> run calls
    /// <see cref="DisableForRun"/>, so it runs from whichever generation is the terminal
    /// one for this invocation — an intermediate generation that hands off to a re-exec'd
    /// child reaches its own <c>ProcessExit</c> only after <c>WaitForExit</c> on that
    /// child returns, i.e. after the child (which inherited the same directory via
    /// <see cref="NoCacheRootEnvVar"/>) is completely done with it. Swallows IO errors —
    /// cleanup failing should never fail the run whose results it is cleaning up after.
    /// </summary>
    public static void CleanupThrowawayRoot()
    {
        if (_throwawayRoot == null) return;
        try
        {
            if (Directory.Exists(_throwawayRoot)) Directory.Delete(_throwawayRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Resolves the on-disk directory for the named cache (e.g. <c>"compiled-deps"</c>).
    /// Returns <c>&lt;override&gt;/&lt;name&gt;</c> when <see cref="SetOverride"/> or
    /// <see cref="DisableForRun"/> was last called with a non-null directory; otherwise
    /// falls back to <c>~/.cache/al-runner/&lt;name&gt;</c>, the pre-#1821 hardcoded
    /// default every one of these caches used unconditionally.
    /// </summary>
    public static string Resolve(string name)
    {
        // AlRunnerPaths.UserHome throws loudly (issue #2114) rather than silently handing
        // back a relative path when $HOME names a directory that does not exist.
        var root = _override ?? Path.Combine(AlRunnerPaths.UserHome, ".cache", "al-runner");
        return Path.Combine(root, name);
    }

    /// <summary>Test-only: resets the override so test processes/hosts that share this
    /// static (e.g. in-process unit tests, as opposed to the spawned-subprocess
    /// integration tests that get natural per-process isolation) don't leak state
    /// between cases.</summary>
    internal static void ResetForTests()
    {
        _override = null;
        _throwawayRoot = null;
    }
}
