// BcEngineSkipReason — the one place a bc-engine-serial skip reason is built.
//
// Why this file exists (issue #3078)
// ----------------------------------
// Every test in the bc-engine-serial collection (BcEngineCollection) skips when
// BcEngineBootstrap could not stand the in-process BC engine up. Skipping is CORRECT —
// a box without the prerequisites genuinely cannot exercise the engine, and failing
// there would make the suite unrunnable for a legitimate reason. The defect is that the
// skip said nothing a developer could act on.
//
// Measured on a local box, 2026-09-06, `dotnet test --filter
// FullyQualifiedName~CodeunitRunWriteTransactionRefusalTests`:
//
//   * At the DEFAULT console verbosity (minimal) VSTest prints `[SKIP]` and
//     `Skipped <name>` and NEVER prints the reason. The run ends
//     `Skipped! - Failed: 0, Passed: 0, Skipped: 5` with exit code 0.
//   * `Console.Error` from the [ModuleInitializer] is swallowed too — the existing
//     `[Cecil] WARNING: Ncl already loaded before in-place rewrite — no effect` line did
//     not reach the console at minimal verbosity either.
//   * Raise verbosity to `normal` and the reason finally appears — and says:
//     `BC engine bootstrap failed: TargetInvocationException: Exception has been thrown
//     by the target of an invocation.`
//
// So the ONE channel that reaches a developer carried a message naming neither the real
// exception (TargetInvocationException.Message is a fixed string; the inner exception is
// the diagnosis and it was dropped), nor which collection had stopped running, nor
// anything to do about it. That is the silent-default shape from
// .claude/rules/loud-failures.md wearing test clothing: the caller cannot distinguish
// "ran and passed" from "did not run".
//
// The fix is not to stop skipping. It is that a skip must be ATTRIBUTABLE: which
// collection, why, and what would make it run. Every reason is built here so that
// property is enforced in one place, and BcEngineSkipAttributionTests fails the build if
// BcEngineCollection.cs ever assigns a reason that bypasses it.

using System.Reflection;

namespace AlRunner.Tests;

/// <summary>
/// Why the in-process BC engine bootstrap did not complete. One value per branch that can
/// leave <see cref="BcEngineBootstrap.Ready"/> false.
/// </summary>
public enum BcEngineSkipCause
{
    /// <summary>BC service-tier artifacts are not provisioned on this machine at all.</summary>
    ArtifactsMissing,

    /// <summary>An artifacts directory exists but does not hold the DLLs the engine needs.</summary>
    ArtifactsIncomplete,

    /// <summary>
    /// Microsoft.Dynamics.Nav.Ncl was already loaded, un-rewritten, before the bootstrap
    /// ran — so the Cecil rewrite could not take effect. Issue #1813: VSTest's own
    /// DiaSession loads Ncl for stack-trace source mapping before any test code runs, and
    /// DOTNET_STARTUP_HOOKS is what gets in front of it.
    /// </summary>
    NclPreloaded,

    /// <summary>
    /// The bootstrap replaced bin/Microsoft.Dynamics.Nav.Ncl.dll in this process, so
    /// loading it here risks BadImageFormatException 0x80131124. Self-healing: the next
    /// run finds bin already rewritten.
    /// </summary>
    BinRewrittenThisProcess,

    /// <summary>
    /// The Ncl Cecil cache missed, so the rewrite ran in this process — same
    /// BadImageFormatException hazard, which is why Program.cs re-execs and a test host
    /// (which cannot re-exec) skips instead.
    /// </summary>
    CecilCacheCold,

    /// <summary>The bootstrap threw. The detail carries the unwrapped exception.</summary>
    BootstrapThrew,

    /// <summary>
    /// <see cref="BcEngineBootstrap.Initialize"/> never ran, so there is no reason recorded
    /// at all — Ready is false purely by default. Reachable whenever something prevents the
    /// [ModuleInitializer] from firing, which is the #1813 shape itself. Its own cause
    /// because the 132 call sites that read the fixture's reason used to fall back to a bare
    /// "the in-process BC engine is not ready (see BcEngineCollection)." here: accurate,
    /// and carrying no cause and no remedy — the same defect as the reason it replaces.
    /// </summary>
    BootstrapDidNotRun,
}

internal static class BcEngineSkipReason
{
    /// <summary>The collection every one of these reasons is about.</summary>
    internal const string Collection = BcEngineCollection.Name;

    /// <summary>
    /// The repo-relative bootstrap tool that makes the engine collection runnable on a
    /// local box. Named by EVERY reason: a remedy the reader has to go and find is the
    /// same silence in a longer sentence. BcEngineSkipAttributionTests asserts this file
    /// actually exists and is executable, because a remedy naming a path that is not there
    /// is worse than none.
    /// </summary>
    internal const string BootstrapTool = "tools/engine-test-bootstrap.sh";

    /// <summary>
    /// The one sentence a skipped bc-engine-serial row prints. Always three things, in
    /// this order: WHICH collection stopped running (and that it asserted nothing), WHY,
    /// and WHAT WOULD MAKE IT RUN. BcEngineSkipAttributionTests asserts all three for
    /// every declared cause.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A cause with no remedy. Deliberately a throw and not a generic fallback string: a
    /// fallback would let a new branch ship the remedy-less reason this whole file exists
    /// to prevent, and it would do it silently (.claude/rules/loud-failures.md).
    /// </exception>
    internal static string Format(BcEngineSkipCause cause, string detail)
    {
        var remedy = Remedy(cause);
        return $"[{Collection}] SKIPPED — this test asserted NOTHING. The in-process BC engine "
             + $"bootstrap did not complete, so every test in the '{Collection}' collection is "
             + $"skipping. Cause ({cause}): {detail} Remedy: {remedy}";
    }

    /// <summary>
    /// The invariant: <c>!Ready</c> implies an ATTRIBUTABLE reason. Every branch of
    /// <see cref="BcEngineBootstrap.Initialize"/> records one, but the initializer not
    /// having run at all records nothing — and the 132 <c>SkipReason ?? "…"</c> call sites
    /// across the collection then printed a bare fallback with no cause and no remedy.
    /// Routing the fixture's own accessor through here closes that hole in one place
    /// instead of at every call site.
    /// </summary>
    internal static string OrDefault(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? Format(BcEngineSkipCause.BootstrapDidNotRun,
                     "BcEngineBootstrap.Initialize recorded no reason, which means it never ran — "
                     + "nothing invoked a member of AlRunner.Tests.dll early enough to trigger its "
                     + "[ModuleInitializer] before the BC engine was needed.")
            : reason;

    private static string Remedy(BcEngineSkipCause cause) => cause switch
    {
        BcEngineSkipCause.ArtifactsMissing or BcEngineSkipCause.ArtifactsIncomplete =>
            "provision the BC service tier with `dotnet build AlRunner.slnx "
            + "-p:AllowBcArtifactDownload=true`, then run `" + BootstrapTool + "` and re-run "
            + "`dotnet test` with `--settings engine.runsettings`.",

        BcEngineSkipCause.NclPreloaded =>
            "DOTNET_STARTUP_HOOKS is not wired into this test host, so VSTest's own DiaSession "
            + "loaded Microsoft.Dynamics.Nav.Ncl before the Cecil rewrite could take effect "
            + "(issue #1813). Run `" + BootstrapTool + "` — it generates engine.runsettings — "
            + "then re-run `dotnet test --settings engine.runsettings`. CI does this in the "
            + "'Generate .runsettings for in-process BC engine tests' step of "
            + ".github/workflows/bc-tests.yml.",

        BcEngineSkipCause.BinRewrittenThisProcess =>
            "this run replaced bin/Microsoft.Dynamics.Nav.Ncl.dll, which happens on the first "
            + "`dotnet test` after every build, so bin is now rewritten and a second run "
            + "executes these tests. `" + BootstrapTool + "` performs that repeat for you and "
            + "reports whether it converged — re-run it after every build.",

        BcEngineSkipCause.CecilCacheCold =>
            "warm the Ncl Cecil cache with one runner invocation before `dotnet test` — `"
            + BootstrapTool + "` does it and repeats until the engine comes up. If it stays "
            + "cold across repeats, the shared ~/.cache/al-runner/ncl-cecil cache is being "
            + "pruned between runs (NclCecilRewrite.PruneCacheFiles keeps only the 8 newest "
            + "entries, and the key folds the runner's own content hash, so more than 8 "
            + "concurrent runner builds evict each other); re-run it or use --cache to give "
            + "this checkout its own cache root.",

        BcEngineSkipCause.BootstrapDidNotRun =>
            "run `" + BootstrapTool + "`. It wires DOTNET_STARTUP_HOOKS through "
            + "engine.runsettings, which is what forces this assembly's [ModuleInitializer] to "
            + "run before the test host touches any BC type (issue #1813).",

        BcEngineSkipCause.BootstrapThrew =>
            "this is not a normal environment gap — the bootstrap raised the exception above. "
            + "Run `" + BootstrapTool + "` for a clean bootstrap; if it still throws, that is a "
            + "runner defect and belongs in an issue rather than being skipped past.",

        _ => throw new ArgumentOutOfRangeException(
                 nameof(cause), cause,
                 $"No remedy declared for BcEngineSkipCause '{cause}'. Every cause must name what "
                 + "would make the " + Collection + " collection run (issue #3078)."),
    };

    /// <summary>
    /// Renders an exception so the DIAGNOSIS survives.
    ///
    /// The bootstrap runs under reflection (a [ModuleInitializer] invoked by the .NET
    /// hosting layer), so its failures arrive wrapped in TargetInvocationException — whose
    /// own Message is the fixed string "Exception has been thrown by the target of an
    /// invocation." Reporting `ex.GetType().Name + ex.Message` therefore printed a sentence
    /// containing no information at all; that was measured verbatim on a local run
    /// (see this file's header). Unwrap to the innermost real exception, and still name the
    /// wrappers so the reflection boundary is not hidden either.
    /// </summary>
    internal static string Describe(Exception ex)
    {
        var wrappers = new List<string>();
        var current = ex;

        while (true)
        {
            Exception? inner = current switch
            {
                TargetInvocationException tie => tie.InnerException,
                // Only a single-inner AggregateException is unwrapped: with several inner
                // exceptions there is no one "real cause" to promote, and picking the first
                // would hide the rest.
                AggregateException { InnerExceptions.Count: 1 } agg => agg.InnerExceptions[0],
                _ => null,
            };

            if (inner is null) break;
            wrappers.Add(current.GetType().Name);
            current = inner;
        }

        var core = $"{current.GetType().Name}: {current.Message}";
        return wrappers.Count == 0 ? core : $"{core} (via {string.Join(" -> ", wrappers)})";
    }
}
