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
    ///
    /// <para><b>Rooted here, once (issue #3084).</b> A <c>--cache</c> value is user-supplied
    /// and may be relative; every path <see cref="Resolve"/> derives from it then stays
    /// relative, and the failure that produces is silent in the worst way. The r2r-chunks
    /// cache feeds <c>AssemblyLoadContext.LoadFromAssemblyPath</c>, which REQUIRES an
    /// absolute path and refuses a relative one — so with <c>--cache .measure/relcache</c>
    /// every extracted chunk of Base Application / System Application / Business Foundation
    /// failed to load, the run dropped to a tier where those apps' objects do not exist, and
    /// it reported ordinary test failures plus 16 <c>[provision-gap]</c> blocks instead of
    /// naming the cache path. Measured on tests/runner-extras/microsoft-test-library, same
    /// build and same package caches: relative <c>--cache</c> 0 pass / 3 fail, absolute
    /// <c>--cache</c> 3 pass / 0 fail.
    ///
    /// Rooted at the WRITE site rather than in <see cref="Resolve"/> for two reasons.
    /// A new named cache added later cannot forget to root itself, the way N read sites
    /// each doing their own <c>Path.GetFullPath</c> can. And rooting here PINS the root to
    /// the working directory as it was when the flag was parsed: <c>Path.GetFullPath</c>
    /// inside <see cref="Resolve"/> would re-resolve against whatever the current directory
    /// happens to be at each call, so a <c>--watch</c> session or anything else that moves
    /// the process's CWD mid-run would move a live run's cache underneath it.
    ///
    /// Same family as issue #2114, which rooted the HOME-derived half of exactly this
    /// problem; <c>--cache</c> is the user-supplied root that sweep did not reach, and it
    /// is strictly worse because #2114 crashed the process where this one degrades quietly.
    /// This changes no persisted cache KEY — no cache resolved here hashes its own root
    /// (r2r-chunks keys on the package content hash, ncl-cecil on the Ncl bytes plus the
    /// runner content hash, and Program.cs's AL-output key deliberately hashes source paths
    /// RELATIVE to the common source root) — and for an unchanged working directory
    /// <c>GetFullPath</c> names the same physical directory the relative form already
    /// resolved to, so entries written by an earlier run under a relative <c>--cache</c>
    /// stay reachable.</para>
    /// </summary>
    public static void SetOverride(string? cacheDir) => _override = Root(cacheDir);

    /// <summary>
    /// <c>Path.GetFullPath</c>, plus a null passthrough for the bare-default case. Both
    /// writers of <see cref="_override"/> go through this so the invariant
    /// "<see cref="_override"/> is absolute or null" holds no matter which one wrote it —
    /// <see cref="DisableForRun"/> mints under <see cref="Path.GetTempPath"/> (already
    /// absolute) but ALSO adopts a value out of the environment, which nothing validates.
    /// </summary>
    private static string? Root(string? dir)
        => string.IsNullOrEmpty(dir) ? dir : Path.GetFullPath(dir);

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
            // #3084: rooted, like every other write to _override. This branch is the ONE
            // path into the override that does not come from Program.cs's flag parsing —
            // it adopts a raw environment value, which nothing validates and which a
            // caller (or a shell that exported a relative path) can set to anything. The
            // sibling branch below is absolute by construction (Path.GetTempPath), so
            // without this the two writers of the same field would not maintain the same
            // invariant, which is exactly how #3084's silent tier-drop got in.
            root = Path.GetFullPath(existing);
            // Republish the rooted form so a child re-exec'd from here inherits an absolute
            // path. Rooting only in this process would leave the child to root the SAME
            // relative value against ITS working directory — the one-throwaway-root-per-RUN
            // guarantee this branch exists to provide would then quietly become two.
            if (!string.Equals(root, existing, StringComparison.Ordinal))
                Environment.SetEnvironmentVariable(NoCacheRootEnvVar, root);
        }
        else
        {
            // Reserved (not created — nothing may ever write into it) through ScratchDirs
            // (#2706) so a run killed before CleanupThrowawayRoot fires is reclaimed by the
            // next runner start instead of leaking a full cache per killed --no-cache run.
            root = Path.GetFullPath(ScratchDirs.Reserve(
                Path.Combine(ThrowawayRootParent, "al-runner-no-cache-" + Guid.NewGuid().ToString("N"))));
            // Publish the ROOTED form, so a re-exec'd child inherits an absolute path
            // even if this generation was handed a relative one (#3084).
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
        ScratchDirs.Release(_throwawayRoot);   // best-effort: deletes the root and its .owner sidecar
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
        // #3084: the invariant, restated where it is consumed. Both writers of _override
        // root what they store, so this cannot fire through SetOverride/DisableForRun —
        // it fires for a THIRD writer added later that forgets to. Stated here, and not
        // only at the write sites, because this one method is the choke point every named
        // cache goes through (compiled-deps, workspace-deps, ncl-cecil, bc-symbols,
        // ncl-shadow, app-manifests, r2r-chunks, install-baseline), so one guard covers
        // all eight rather than each consumer having to know that LoadFromAssemblyPath —
        // or a re-exec, or a path-prefix comparison — is downstream of it.
        //
        // #3111, inventory: r2r-chunks is not the only consumer that would break. ncl-shadow
        // is resolved by NclShadowRuntime.EnsureShadowDir and the dll under it is added to a
        // child process's ArgumentList at ProgramSupport/Provisioning.cs:106 (`dotnet exec
        // <dll>`) — a relative root there is resolved by the CHILD, against whatever working
        // directory the child happens to inherit, so it is undefended for a reason unrelated
        // to assembly loading. Both consequences are named in the message below.
        //
        // Loud rather than best-effort-rooted-here on purpose (.claude/rules/loud-failures.md):
        // silently calling Path.GetFullPath at THIS point would re-resolve against whatever
        // the current directory is right now, which is the moving-target bug the write-site
        // rooting exists to prevent, and would hide the defect that produced the unrooted
        // value instead of naming it.
        //
        // #3111: the check itself moved into RequireRooted so the ONE cache root that
        // deliberately does not come through here — Program.cs's alCacheDir, see this
        // class's "Deliberately NOT wired into alCacheDir" note — can assert the same
        // invariant with the same wording instead of having no guard at all.
        RequireRooted(root, name);
        return Path.Combine(root, name);
    }

    /// <summary>
    /// Throws unless <paramref name="dir"/> is absolute; returns it unchanged when it is.
    ///
    /// <para>Extracted from <see cref="Resolve"/> in #3111 because the guard #3084 added
    /// covered every cache that flows through <see cref="Resolve"/> and NOTHING else, and
    /// there is exactly one cache root that does not flow through it: the AL-output
    /// directory (<c>Program.cs</c>'s <c>alCacheDir</c>), which keeps exact-directory
    /// semantics on purpose and therefore never asks this class for a path. That left the
    /// two halves of one <c>--cache</c> value under different rules — the derived roots
    /// guarded, the al-out root not — which is the same "two code paths write the same
    /// state, only one holds the invariant" shape #3084's <see cref="DisableForRun"/> half
    /// was about. Call it from any consumer that holds a cache root it did not get from
    /// <see cref="Resolve"/>.</para>
    /// </summary>
    /// <param name="dir">The cache root to check.</param>
    /// <param name="name">The cache's name, for the diagnostic (e.g. <c>"r2r-chunks"</c>,
    /// <c>"al-out"</c>).</param>
    internal static string RequireRooted(string dir, string name)
        => Path.IsPathRooted(dir)
            ? dir
            : throw new InvalidOperationException(BuildUnrootedCacheRootMessage(dir, name));

    /// <summary>
    /// The directory <see cref="DisableForRun"/> mints its throwaway root under when
    /// <see cref="NoCacheRootEnvVar"/> is unset. A property rather than a second inline
    /// <c>Path.GetTempPath()</c> so the error path below names the directory the mint
    /// actually failed under, instead of a second copy of the expression that could drift
    /// from it — the same "two code paths, one invariant" hygiene the rest of this class
    /// is about.
    /// </summary>
    internal static string ThrowawayRootParent => Path.GetTempPath();

    /// <summary>
    /// The diagnostic for the OTHER way <see cref="DisableForRun"/> can fail: the branch
    /// that mints a throwaway root itself, reached only when <see cref="NoCacheRootEnvVar"/>
    /// is unset.
    ///
    /// <para>Separate from <see cref="BuildUnusableCacheRootMessage"/> because that one
    /// names a SOURCE and a VALUE, and this branch has neither — nothing supplied a path,
    /// the runner chose one. #3111's first cut reused it anyway and reported
    /// <c>AL_RUNNER_NO_CACHE_ROOT '' is not a usable directory path: …</c>, naming a
    /// variable the user never set and quoting an empty value that was never anybody's
    /// input. An error that names a cause which did not happen is worse than a generic
    /// one: it sends the reader to unset a variable that is already unset.</para>
    /// </summary>
    /// <param name="parent">The directory the mint was attempted under, normally
    /// <see cref="ThrowawayRootParent"/>.</param>
    /// <param name="detail">The underlying failure, normally the exception's message.</param>
    internal static string BuildUnreservableThrowawayRootMessage(string parent, string detail)
        => $"al-runner could not reserve a throwaway --no-cache root under '{parent}': " +
           $"{detail}. Set {NoCacheRootEnvVar} to a writable absolute directory, or drop " +
           $"--no-cache to use the normal cache root.";

    /// <summary>
    /// One wording for "this cache root could not even be turned into a usable path", shared
    /// by all three writers of a cache root at startup (#3111): <c>Program.cs</c>'s
    /// <c>--cache</c> parsing, its <see cref="DisableForRun"/> call, and its
    /// <see cref="SetOverride"/> call. They previously had one inline message between them
    /// and no message at all on the other two paths, which is how the
    /// <see cref="DisableForRun"/> path came to abort the process with an unhandled
    /// exception (exit 134) where <c>--cache</c> returned the documented exit 2 — the exact
    /// asymmetry #2114 is about, one flag over.
    /// </summary>
    /// <param name="source">What supplied the value, spelled the way the user typed it:
    /// <c>"--cache"</c>, or the environment variable's name.</param>
    /// <param name="value">The offending value, quoted into the message verbatim.</param>
    /// <param name="detail">The underlying failure, normally the exception's message.</param>
    internal static string BuildUnusableCacheRootMessage(string source, string value, string detail)
        => $"{source} '{value}' is not a usable directory path: {detail}";

    /// <summary>
    /// The diagnostic for a cache root that is not absolute. Separate and internal so the
    /// tests can assert the exact text without reaching into <see cref="Resolve"/>'s
    /// control flow. Names the value, the flag that most likely supplied it, and — the part
    /// that was missing in #3084 — the consequence, so the reader is not left to conclude
    /// from 16 <c>[provision-gap]</c> blocks that their package cache is unprovisioned.
    /// </summary>
    internal static string BuildUnrootedCacheRootMessage(string root, string name)
        => $"al-runner resolved the '{name}' cache to a RELATIVE root '{root}'. Every cache " +
           $"root must be absolute: the r2r-chunks cache feeds " +
           $"AssemblyLoadContext.LoadFromAssemblyPath, which refuses a relative path, and a " +
           $"relative root also means a different directory for any part of the run that " +
           $"executes from a different working directory — the ncl-shadow root is handed to " +
           $"a CHILD process as a `dotnet exec` argument (ProgramSupport/Provisioning.cs), " +
           $"which is a second consumer that cannot survive one. Left unchecked this does not fail " +
           $"the load loudly — it drops Base Application / System Application / Business " +
           $"Foundation to a lower tier where their objects do not exist, and the run then " +
           $"reports ordinary test failures. Pass an absolute directory to --cache (issue #3084).";

    /// <summary>
    /// Test-only seam for the <see cref="Resolve"/> rootedness guard above. Both production
    /// writers of the override root what they store, so the guard is unreachable through
    /// them by construction — and a guard with no test is a guard nobody knows still works.
    /// Same shape, and the same reason, as
    /// <c>AppLoader.ExtractAllDllPathsCore</c>'s internal content-hash-provider overload,
    /// which exists so the identity tests can drive its no-identity branch.
    /// </summary>
    internal static void SetOverrideBypassingRootingForTests(string cacheDir) => _override = cacheDir;

    /// <summary>Test-only: resets the override so test processes/hosts that share this
    /// static (e.g. in-process unit tests, as opposed to the spawned-subprocess
    /// integration tests that get natural per-process isolation) don't leak state
    /// between cases.</summary>
    internal static void ResetForTests()
    {
        _override = null;
        _throwawayRoot = null;
    }

    /// <summary>
    /// The roots under which this run writes packages it built FROM SOURCE — SiblingCompile's
    /// synthesized workspace dirs (both <c>RunLayeredPrePass</c> and
    /// <c>BuildSiblingSourceDeps</c> write under "workspace-deps"). Handed to
    /// <see cref="AlRunner.DependencyResolver"/> so a source build outranks a packaged copy of
    /// the same app instead of losing to it on version (#2688). A root rather than the
    /// per-request list of dirs: the resolver matches by path prefix, and a per-request list
    /// leaves a PRIOR request's dirs unmarked in a warm --server/--watch session, which is the
    /// same scoping mistake Program.cs's selectionEnvironmentKey comment records.
    /// </summary>
    public static IReadOnlyList<string> SourceBuiltPackageDirs()
        => new[] { Resolve("workspace-deps") };
}
