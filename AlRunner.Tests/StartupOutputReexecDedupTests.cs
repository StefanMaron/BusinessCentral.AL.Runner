// StartupOutputReexecDedupTests — issues #2041 and #2066.
//
// Split out of #2037/#2038: this process's startup reporting (the `[provision] BC ...
// already complete` line, `[bc] selected BC ...`, and the `al-runner — running N
// bundle(s)` banner) is printed BEFORE the shadow-re-exec decision hands off to a child
// process. The tool package no longer ships Microsoft.Dynamics.Nav.Ncl.dll (#2023/#2026),
// so NclShadowRuntime.NeedsShadow is true on essentially every real invocation, and the
// child re-runs the exact same startup sequence from scratch — reprinting all three
// lines. Confirmed live against the published 2.5.0 package with `strace -f -e
// trace=execve`: exactly two execve calls on a warm run, and all three lines appear
// twice, once per process generation.
//
// #2041's original fix computed whether THIS generation would need to shadow-re-exec — a
// cheap, deterministic filesystem check (does Ncl.dll already exist beside this
// assembly?) known before any of the three lines would print — and suppressed them in
// that generation only. That covered exactly the one re-exec it was written for. #2066
// (published 2.7.0) found a SECOND re-exec that can stack on top — a per-BC-minor
// engine-variant swap, or (the shape this file's tests reproduce deterministically) the
// shadow-hop generation's own first-ever Cecil rewrite landing a cache MISS — which the
// #2041 flag had no way to predict, since it is only knowable after the shadow hop has
// already happened. Program.cs's fix (see its own comments starting at
// `deferredStartupLines`) replaced predict-then-suppress with defer-then-flush: the three
// lines (and the degraded-variant warning) are queued rather than printed immediately,
// and flushed only once a generation clears EVERY re-exec decision point in the
// function — a generation that re-execs further never reaches the flush and its queue is
// simply discarded, so this now generalizes to however many generations stack, not just
// the one #2041 anticipated. The `[reexec]` explanation itself (#2034/#2038) is
// untouched: it still prints unconditionally from whichever generation decides to hand
// off, at default verbosity.
//
// Issue #2061 — shared mutable state between this class's tests, round 2
// -------------------------------------------------------------------------------------
// Two of this class's tests need a bin/-shaped directory where Ncl.dll is genuinely
// present (NoReexecRun_...) or absent-then-forced (ShadowDoneEnvVarForced_...). Both
// used to run directly against the SHARED AlRunner/bin/<config>/<tfm>/ directory that
// Program.cs itself is built into — the same directory several OTHER test classes in
// this assembly also spawn the real runner against concurrently (see
// NclShadowRuntime.EnsureShadowDir's own doc comment on why its shadow-dir cache key
// folds in a hash of the caller's directory: "AlRunner.Tests's own parallel test
// collections proved this matters").
//
// A first pass at #2061 added a precondition assert (Ncl.dll must be absent before
// NoReexecRun_...'s warmup spawn) plus a loud, retrying cleanup in
// ShadowDoneEnvVarForced_... in place of a swallowed `catch { /* best effort */ }`. Both
// were correct fixes in isolation, but PR #2063's own CI run (BC 27.3 leg) proved they
// were not sufficient: the precondition passed, yet the warmup spawn's OWN re-check of
// the identical path, moments later inside a freshly cold-started subprocess, found
// Ncl.dll already there — and the TRX for that run showed zero wall-clock overlap
// between the two tests, so it was not simple same-class scheduling. The real problem is
// structural: a check here and a re-check inside a separate process launch are two
// different moments in time, against a path this test does not own exclusively — no
// amount of asserting the state at ONE of those moments closes a gap that exists BETWEEN
// them.
//
// So both tests now build their OWN private, uniquely-named mirror of the real build
// output (MirrorOriginalBinDir, reusing NclShadowRuntime.MirrorInstallDirectory — the
// exact mechanism Program.cs's own shadow-dir builder uses: dependency DLLs symlinked,
// the entry assembly and its manifests real-copied) and run every remaining step against
// that private copy. Once mirrored, nothing else in the process — no sibling test, no
// concurrently-running test class, no subprocess this test itself spawns — can ever
// observe or mutate the path this test cares about, which removes the shared-state
// hazard structurally rather than narrowing the window around it.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class StartupOutputReexecDedupTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string Fixture =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>
    /// Acceptance #1 + #2: a WARM run (ncl-cecil cache already populated — the exact
    /// shape of the issue's own repro, "two execve calls") against an explicit
    /// --bc-version prints the provisioning line, the `[bc] selected BC` line and the
    /// banner exactly once each, and the `[reexec]` explanation still fires from the
    /// parent — all at DEFAULT verbosity (no AL_RUNNER_VERBOSE), since #2038 already
    /// made `[provision]`/`[bc]`/`[reexec]` survive the default-verbosity filter and
    /// this is what a real user actually sees.
    ///
    /// Unlike its two siblings below, this test does not need Ncl.dll's presence/absence
    /// pinned to a specific value — either a genuine re-exec (trio suppressed once,
    /// printed once by the child) or a no-re-exec run (trio printed once, directly) still
    /// satisfies every assertion here, so it is naturally unaffected by the shared-state
    /// hazard #2061 is about and needs no private mirror.
    /// </summary>
    [SkippableFact]
    public void WarmRun_PrintsStartupTrioExactlyOnce_ReexecExplanationStillFromParent()
    {
        TestArtifacts.SkipIfMissing();

        // Warm-up spawn: primes the ncl-cecil / ncl-shadow caches so the run asserted
        // on below is genuinely warm (a cold cache adds a THIRD generation via the
        // fresh-Cecil-rewrite re-exec — that stacked, three-generation shape is what
        // ColdCecilCache_StackedShadowAndFreshRewriteReexec_... below covers instead).
        Spawn();

        var (output, exit) = Spawn();
        Assert.Equal(0, exit);
        Assert.Contains("pass:        1", output);
        Assert.Contains("fail:        0", output);

        Assert.Equal(1, CountOccurrences(output, "[provision] BC "));
        Assert.Equal(1, CountOccurrences(output, "[bc] selected BC "));
        Assert.Equal(1, CountOccurrences(output, "al-runner — running "));

        // The re-exec explanation must not have been collapsed away along with the
        // duplicated trio above — it is specifically about the parent and stays there.
        Assert.Equal(1, CountOccurrences(output, "[reexec] Ncl.dll not shipped in this install"));
    }

    /// <summary>
    /// Acceptance #3: a run that does NOT need to re-exec at all — spawned directly out
    /// of the shadow runtime dir, which genuinely has Ncl.dll on disk, so
    /// NclShadowRuntime.NeedsShadow is false for it — is unaffected by the fix: the trio
    /// still prints, exactly once, same as it always did, and no `[reexec]` marker
    /// appears since none fired.
    /// </summary>
    [SkippableFact]
    public void NoReexecRun_StartupTrioUnchanged()
    {
        TestArtifacts.SkipIfMissing();

        // See the file header (#2061) for why this runs against a private mirror rather
        // than the shared AlRunner/bin/.../ directory.
        var privateDir = MirrorOriginalBinDir();
        try
        {
            var privateDll = Path.Combine(privateDir, "al-runner.dll");

            // Discover the shadow dir path from a REAL subprocess's own [Cecil] "Building/
            // Reusing Ncl shadow runtime dir at ..." line (verbose only for this discovery
            // spawn) rather than calling NclShadowRuntime.EnsureShadowDir in-process: this
            // test host process has almost certainly already loaded
            // Microsoft.Dynamics.Nav.Ncl.dll for some OTHER test in the same run, and
            // NclCecilRewrite.RewriteInPlace silently no-ops ("Ncl already loaded before
            // in-place rewrite — no effect") whenever that is true for the CURRENT
            // AppDomain — it would build a shadow dir with every dependency mirrored except
            // the one file this test is actually about. A fresh child process never has
            // that problem, and it is exactly the path Program.cs itself takes.
            var (warmupOutput, warmupExit) = SpawnVerboseAssembly(privateDll);
            Assert.Equal(0, warmupExit);
            var shadowDir = ExtractShadowDir(warmupOutput);
            var shadowDll = Path.Combine(shadowDir, "al-runner.dll");
            Assert.True(File.Exists(shadowDll), $"shadow al-runner.dll not found at {shadowDll}");
            Assert.True(
                File.Exists(Path.Combine(shadowDir, "Microsoft.Dynamics.Nav.Ncl.dll")),
                "shadow dir is missing its own Ncl.dll copy — NeedsShadow would be true there too");

            var (output, exit) = SpawnAssembly(shadowDll);
            Assert.Equal(0, exit);
            Assert.Contains("pass:        1", output);
            Assert.Contains("fail:        0", output);

            Assert.Equal(1, CountOccurrences(output, "[provision] BC "));
            Assert.Equal(1, CountOccurrences(output, "[bc] selected BC "));
            Assert.Equal(1, CountOccurrences(output, "al-runner — running "));
            Assert.DoesNotContain("[reexec]", output);
        }
        finally
        {
            DeleteDirectoryOrFail(privateDir);
        }
    }

    /// <summary>
    /// Issue #2066: a run that stacks TWO re-execs — the shadow hop (Ncl.dll not shipped
    /// beside this assembly) AND the Cecil-fresh-rewrite hop (cold ncl-cecil cache) — must
    /// still print the startup trio and the `[reexec]` explanations from BOTH hops exactly
    /// once each, across all three process generations.
    ///
    /// #2044's fix (see the file header above and Program.cs's history) predicted whether
    /// the CURRENT generation would need to shadow-re-exec, using a flag computed BEFORE
    /// the Cecil-rewrite decision could be known, and suppressed the trio in that
    /// generation only. That covers exactly one re-exec. The reported real-world failure
    /// (#2066) stacks a second one on top: the shadow-hop generation itself, once landed,
    /// still needs to perform its OWN first-ever Cecil rewrite — a genuine cache MISS —
    /// which forces a THIRD process generation. The flag from #2044 had already decided
    /// (wrongly, for that middle generation) that no further re-exec was coming, so the
    /// middle generation printed the trio believing itself final, then re-exec'd anyway,
    /// and the true final generation printed it again: three generations, two prints.
    ///
    /// AL_RUNNER_NCL_CACHE=0 forces NclCecilRewrite.RewriteInPlace to treat EVERY call as a
    /// fresh rewrite (bypassing the ncl-cecil cache read/write entirely — see
    /// NclCecilRewrite.RewriteInPlace's own doc comment), which reliably reproduces the
    /// exact "middle generation's own first rewrite is a cache MISS" shape without needing
    /// a packaged per-BC-minor engine variant (the OTHER way #2066 observed three
    /// generations) — both routes exercise the identical code path once the shadow hop has
    /// landed, since EnsureShadowDir's own pre-rewrite (used for the "Ncl.dll not shipped"
    /// case) is a completely separate call from the child's later RewriteInPlace at the top
    /// of Program.cs, and it is that second call's cache-MISS decision this test pins.
    ///
    /// Confirmed as a genuine three-generation run (not an artifact of forcing the flag):
    /// asserts both `[reexec]` explanation lines are present, each exactly once — one per
    /// re-exec that actually fired.
    /// </summary>
    [SkippableFact]
    public void ColdCecilCache_StackedShadowAndFreshRewriteReexec_StartupTrioPrintsExactlyOnce()
    {
        TestArtifacts.SkipIfMissing();

        // See the file header (#2061) for why this runs against a private mirror rather
        // than the shared AlRunner/bin/.../ directory. This mirror genuinely lacks
        // Ncl.dll (same precondition MirrorOriginalBinDir already asserts), so the shadow
        // hop is guaranteed to fire for it regardless of any other test's state.
        var privateDir = MirrorOriginalBinDir();
        try
        {
            var privateDll = Path.Combine(privateDir, "al-runner.dll");

            var psi = BuildPsi(privateDll);
            // Forces a fresh Cecil rewrite in EVERY generation that reaches
            // NclCecilRewrite.RewriteInPlace, including the shadow-hop child's own
            // first-start rewrite — the deterministic stand-in for a genuinely cold
            // ncl-cecil cache, reproducing the exact stacked scenario without racing
            // the shared, machine-wide ncl-cecil cache directory other concurrently
            // running tests/processes also read and write.
            psi.Environment["AL_RUNNER_NCL_CACHE"] = "0";
            var (output, exit) = Run(psi);

            Assert.Equal(0, exit);
            Assert.Contains("pass:        1", output);
            Assert.Contains("fail:        0", output);

            // Both re-exec triggers genuinely fired — three generations, not one or two.
            Assert.Equal(1, CountOccurrences(output, "[reexec] Ncl.dll not shipped in this install"));
            Assert.Equal(1, CountOccurrences(output, "[reexec] Fresh rewrite done — re-execing for a clean Ncl load"));

            // The startup trio survives all three generations and prints exactly once,
            // from the third (truly terminal) generation only.
            Assert.Equal(1, CountOccurrences(output, "[provision] BC "));
            Assert.Equal(1, CountOccurrences(output, "[bc] selected BC "));
            Assert.Equal(1, CountOccurrences(output, "al-runner — running "));
        }
        finally
        {
            // RewriteInPlace writes Ncl.dll directly into the private mirror (top-level
            // dir) as a side effect of the top-level generation's own rewrite, same as
            // ShadowDoneEnvVarForced_... below — clean up the whole mirror.
            DeleteDirectoryOrFail(privateDir);
        }
    }

    /// <summary>
    /// Issue #2097 — #2066 deferred the startup trio, but two more steady-state
    /// informational lines on the exact same startup path were left unconditional and
    /// duplicated once per process generation the same way: the "[bc] no --bc-version
    /// given — selecting BC ..." auto-selection line, and the "[expectations] loaded N
    /// entries from ..." manifest line. Both are printed BEFORE the shadow-re-exec
    /// decision (unlike the trio, before `deferredStartupLines` even existed prior to
    /// this fix), so the same stacked-three-generation shape
    /// (ColdCecilCache_StackedShadowAndFreshRewriteReexec_... above) reprints them twice
    /// too many.
    ///
    /// Unlike that sibling test, this one must NOT pass an explicit --bc-version — the
    /// auto-selection line only prints when none is given (`bcVersionArg == null &&
    /// artifactPathArg == null`), which is also the realistic case #2097's own
    /// measurement used. This dev/CI environment always has the exact engine build
    /// cached (TestArtifacts.SkipIfMissing's own precondition), so BcArtifacts.
    /// DefaultVersionPrefix resolves the "cached-exact" tier deterministically —
    /// the same branch #2097 measured against.
    ///
    /// Asserts both `[reexec]` lines first, exactly once each — proof this genuinely ran
    /// three generations, not that nothing re-exec'd at all (a test that skipped that
    /// check could pass by accident on a single-generation run where nothing had a
    /// chance to duplicate).
    ///
    /// Only the "cached-exact"/"cached-minor" branches of Program.cs's BC-selection
    /// switch and the "loaded" (not "not found") expectations line are actually
    /// deferred by the fix — see Program.cs's own comments at each site for why the
    /// "cdn-exact"/"cdn-minor"/KNOWN-DEGRADED branches and the --tdd cache-disable
    /// notice deliberately stay immediate instead (deferring them broke
    /// DefaultProvisionTargetMessagingTests, which pins their immediate-print
    /// contract). This test only exercises the "cached-exact" branch, matching #2097's
    /// own measured repro.
    /// </summary>
    [SkippableFact]
    public void ColdCecilCache_StackedShadowAndFreshRewriteReexec_BcAutoSelectAndExpectationsPrintExactlyOnce()
    {
        TestArtifacts.SkipIfMissing();

        // See the file header (#2061) for why this runs against a private mirror rather
        // than the shared AlRunner/bin/.../ directory.
        var privateDir = MirrorOriginalBinDir();
        try
        {
            var privateDll = Path.Combine(privateDir, "al-runner.dll");

            var psi = BuildPsiWithoutBcVersion(privateDll);
            // Same forced-cold-cecil-cache technique as the sibling test above — see its
            // own doc comment for why this reliably reproduces the stacked
            // shadow-hop-then-fresh-rewrite, three-generation shape.
            psi.Environment["AL_RUNNER_NCL_CACHE"] = "0";
            var (output, exit) = Run(psi);

            Assert.Equal(0, exit);
            Assert.Contains("pass:        1", output);
            Assert.Contains("fail:        0", output);

            // Confirm three generations genuinely happened before trusting any of the
            // exactly-once counts below — the same guard the sibling test above uses.
            Assert.Equal(1, CountOccurrences(output, "[reexec] Ncl.dll not shipped in this install"));
            Assert.Equal(1, CountOccurrences(output, "[reexec] Fresh rewrite done — re-execing for a clean Ncl load"));

            // The two lines #2097 reported as still duplicating per generation.
            Assert.Equal(1, CountOccurrences(output, "[bc] no --bc-version given — selecting BC "));
            Assert.Equal(1, CountOccurrences(output, "[expectations] loaded "));
        }
        finally
        {
            // RewriteInPlace writes Ncl.dll directly into the private mirror (top-level
            // dir) as a side effect of the top-level generation's own rewrite, same as
            // the sibling tests above — clean up the whole mirror.
            DeleteDirectoryOrFail(privateDir);
        }
    }

    /// <summary>
    /// Regression: `reexecPending` must track the ACTUAL re-exec gate
    /// (`NeedsShadow(...) && AL_RUNNER_NCL_SHADOW_DONE != "1"`), not `NeedsShadow` alone.
    ///
    /// Setup: Ncl.dll is genuinely absent from a bin/-shaped directory — so NeedsShadow
    /// is true — but AL_RUNNER_NCL_SHADOW_DONE=1 is forced by hand (a plausible way to
    /// skip the shadow hop while debugging), and the ncl-cecil cache is already warm for
    /// this exact build (primed by the earlier warm-up spawns in this class). Under that
    /// combination: the shadow-re-exec block is skipped (env guard),
    /// NclCecilRewrite.RewriteInPlace hits the warm cache and just copies the cached
    /// bytes into place (no further re-exec — it only re-execs on a genuine cache MISS),
    /// so this single process runs the whole bundle itself. ZERO re-execs happen at all.
    ///
    /// If `reexecPending` were computed from `NeedsShadow` alone (ignoring the env
    /// guard), it would read true here even though no re-exec follows — suppressing the
    /// provisioning line, `[bc] selected BC`, and the banner in the ONLY generation that
    /// ever runs, with no later generation to reprint them. Confirmed by reverting the
    /// env-guard clause locally: the trio does not appear ANYWHERE in the output in that
    /// configuration, even though the run itself passes cleanly (same silent-output
    /// class of bug #2034 was about, one file over).
    /// </summary>
    [SkippableFact]
    public void ShadowDoneEnvVarForced_NoFurtherReexecFollows_StartupTrioStillPrintsOnce()
    {
        TestArtifacts.SkipIfMissing();

        // See the file header (#2061) for why this runs against a private mirror rather
        // than the shared AlRunner/bin/.../ directory.
        var privateDir = MirrorOriginalBinDir();
        try
        {
            var privateDll = Path.Combine(privateDir, "al-runner.dll");
            var privateNcl = Path.Combine(privateDir, "Microsoft.Dynamics.Nav.Ncl.dll");

            // Warm the ncl-cecil cache for this exact build (normal spawn, via ITS OWN
            // shadow dir — never touches the private mirror's own bin/ built above) so
            // the forced run below hits a cache HIT rather than a genuine MISS (a MISS
            // would trigger the UNRELATED fresh-rewrite re-exec — see Program.cs — which
            // would mask exactly the single-generation scenario this test is pinning).
            SpawnAssembly(privateDll);
            Assert.False(File.Exists(privateNcl),
                $"precondition violated: {privateNcl} already exists — NeedsShadow would be " +
                "false for the mirror regardless of the env guard, and this test would not be " +
                "exercising the scenario it claims to.");

            var psi = BuildPsi(privateDll);
            psi.Environment["AL_RUNNER_NCL_SHADOW_DONE"] = "1";
            var (output, exit) = Run(psi);

            Assert.Equal(0, exit);
            Assert.Contains("pass:        1", output);
            Assert.Contains("fail:        0", output);

            // Zero re-execs of ANY kind fired — this really is the single-generation
            // case, not the fresh-rewrite one masking it.
            Assert.DoesNotContain("[reexec]", output);

            Assert.Equal(1, CountOccurrences(output, "[provision] BC "));
            Assert.Equal(1, CountOccurrences(output, "[bc] selected BC "));
            Assert.Equal(1, CountOccurrences(output, "al-runner — running "));
        }
        finally
        {
            // RewriteInPlace writes Ncl.dll directly into the private mirror as a side
            // effect of this scenario (see the doc comment above) — clean up the WHOLE
            // mirror (it is exclusively this test's own, private, uniquely-named copy;
            // nothing else references it).
            //
            // Issue #2061: this used to be `try { File.Delete(originalNcl); } catch
            // { /* best effort */ }` against the SHARED bin/ directory. A test that
            // cannot restore state it mutated has failed, even though its own assertions
            // above passed — so this is no longer best-effort, and (round 2) it is no
            // longer shared state either.
            DeleteDirectoryOrFail(privateDir);
        }
    }

    /// <summary>
    /// Issue #2061, acceptance #2: a cleanup that genuinely cannot delete what it created
    /// must fail the test loudly, naming the path and the underlying exception — not
    /// swallow the failure the way the old `catch { /* best effort */ }` did. Simulates an
    /// undeletable directory the same way a real dev box would produce one
    /// deterministically (removing write permission on the directory itself — deleting an
    /// entry FROM a directory requires write permission on that directory, not on the
    /// entry) rather than relying on a same-process file lock, which .NET's own docs admit
    /// does not reliably block a delete on non-Windows platforms including the
    /// ubuntu-latest CI runner this suite targets.
    /// </summary>
    [Fact]
    public void DeleteDirectoryOrFail_UndeletableDirectory_FailsLoudlyNamingDirectoryAndException()
    {
        var dir = Directory.CreateTempSubdirectory("al-runner-deletedirorfail-").FullName;
        File.WriteAllText(Path.Combine(dir, "Microsoft.Dynamics.Nav.Ncl.dll"), "not a real dll — just needs to exist");
        try
        {
            RunChmod("a-w", dir); // remove write permission on the directory itself, so
                                   // its own entry (the file above) can no longer be unlinked.

            var thrown = Assert.ThrowsAny<Exception>(() => DeleteDirectoryOrFail(dir));

            // Both the exact directory path and *something* naming the underlying cause
            // must be in the failure — a message like "cleanup failed" alone would still
            // pass a version of this test that hardcoded a generic string, so pin the
            // actual exception type name too (UnauthorizedAccessException on Linux for a
            // read-only-directory delete).
            Assert.Contains(dir, thrown.Message);
            Assert.Contains("UnauthorizedAccessException", thrown.Message);
            // The directory must still exist — DeleteDirectoryOrFail must not have
            // silently swallowed the failure and let the caller believe cleanup succeeded.
            Assert.True(Directory.Exists(dir),
                "DeleteDirectoryOrFail must leave the undeletable directory in place, not pretend it was removed");
        }
        finally
        {
            RunChmod("u+w", dir);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Negative-of-the-negative: once the directory CAN be deleted, DeleteDirectoryOrFail
    /// must actually delete it and return normally rather than always failing loudly
    /// regardless of outcome (which would trivially "pass" the test above for the wrong
    /// reason).
    /// </summary>
    [Fact]
    public void DeleteDirectoryOrFail_DeletableDirectory_DeletesAndReturns()
    {
        var dir = Directory.CreateTempSubdirectory("al-runner-deletedirorfail-ok-").FullName;
        File.WriteAllText(Path.Combine(dir, "Microsoft.Dynamics.Nav.Ncl.dll"), "not a real dll — just needs to exist");

        DeleteDirectoryOrFail(dir);

        Assert.False(Directory.Exists(dir), "DeleteDirectoryOrFail should have deleted a genuinely deletable directory");
    }

    private static void RunChmod(string mode, string path)
    {
        using var p = Process.Start(new ProcessStartInfo("chmod", $"{mode} \"{path}\"")
        {
            UseShellExecute = false,
        })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    /// <summary>
    /// Deletes the directory at <paramref name="dir"/> (recursively), retrying briefly to
    /// absorb a transient lock (e.g. a not-yet-terminated child process still holding a
    /// file inside it open). If it still cannot be deleted after retrying, this fails the
    /// CALLING test loudly, naming the directory and the underlying exception, rather than
    /// swallowing the failure: a test that cannot restore file-system state it created has
    /// failed, even if its own assertions passed (issue #2061).
    /// </summary>
    private static void DeleteDirectoryOrFail(string dir)
    {
        const int maxAttempts = 5;
        Exception? last = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                Directory.Delete(dir, recursive: true);
                if (!Directory.Exists(dir)) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
            }
            Thread.Sleep(100);
        }

        Assert.Fail(
            $"cleanup failed: could not delete directory {dir} after {maxAttempts} attempts. " +
            "This private mirror was meant to be owned exclusively by the test that created " +
            "it, so a failure removing it now means something is still holding one of its " +
            $"files open. Underlying exception: {last}");
    }

    /// <summary>Extracts the path from Program's own `[Cecil] Building/Reusing Ncl
    /// shadow runtime dir at &lt;path&gt;` line.</summary>
    private static string ExtractShadowDir(string output)
    {
        var m = Regex.Match(output, @"\[Cecil\] (?:Building|Reusing) Ncl shadow runtime dir at (.+)$",
            RegexOptions.Multiline);
        Assert.True(m.Success, $"could not find the shadow-dir marker line in runner output:\n{output}");
        return m.Groups[1].Value.TrimEnd('\r');
    }

    /// <summary>
    /// Mirrors the runner's real build output directory (AlRunner/bin/&lt;config&gt;/
    /// &lt;tfm&gt;/) into a fresh, uniquely-named private directory, using the exact same
    /// mechanism Program.cs's own shadow-dir builder uses
    /// (NclShadowRuntime.MirrorInstallDirectory — dependency DLLs symlinked at near-zero
    /// cost, the entry assembly and its manifests real-copied). See the file header
    /// (issue #2061) for why the tests below need this instead of spawning directly
    /// against the shared directory.
    ///
    /// Asserts the SOURCE directory's own Ncl.dll is absent before mirroring — issue
    /// #2061 acceptance #3. Nothing in this file writes to the shared source directory
    /// any more, so this should always hold; it exists to turn a contaminated SOURCE
    /// (e.g. a stray Ncl.dll left over from manually reproducing this issue by hand, or
    /// from some future change that regresses this file's own isolation) into an
    /// immediate, named precondition failure instead of a confusing one down inside
    /// ExtractShadowDir — the mirror would otherwise faithfully copy the contamination
    /// forward and this test would silently stop exercising the scenario it claims to.
    /// </summary>
    private static string MirrorOriginalBinDir()
    {
        var originalBinDir = Path.Combine(
            ProjectPath, "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework);
        var originalNcl = Path.Combine(originalBinDir, "Microsoft.Dynamics.Nav.Ncl.dll");
        Assert.False(File.Exists(originalNcl),
            $"precondition violated: {originalNcl} already exists in the runner's shared build " +
            "output directory. Every test in this class now mirrors that directory into its own " +
            "private copy rather than mutating it directly (see issue #2061), so this should never " +
            "happen — if it does, something outside this file (or a manual repro left behind by " +
            "hand) has contaminated the shared source, and the private mirror below would " +
            "otherwise silently copy that contamination forward.");

        var privateDir = Directory.CreateTempSubdirectory("al-runner-startup-mirror-").FullName;
        NclShadowRuntime.MirrorInstallDirectory(originalBinDir, privateDir);
        return privateDir;
    }

    private (string Output, int Exit) Spawn() =>
        SpawnAssembly(Path.Combine(
            ProjectPath, "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework, "al-runner.dll"));

    /// <summary>Same spawn as <see cref="SpawnAssembly"/>, but AL_RUNNER_VERBOSE=1 so the
    /// `[Cecil]`-tagged shadow-dir marker line (suppressed by default — see Log.cs) is
    /// observable, purely for path discovery. Not used for any of the count assertions,
    /// which must stay at default verbosity to prove what a real user actually sees.
    /// </summary>
    private (string Output, int Exit) SpawnVerboseAssembly(string dllPath)
    {
        var psi = BuildPsi(dllPath);
        psi.Environment["AL_RUNNER_VERBOSE"] = "1";
        return Run(psi);
    }

    private (string Output, int Exit) SpawnAssembly(string dllPath) => Run(BuildPsi(dllPath));

    private ProcessStartInfo BuildPsi(string dllPath) =>
        BuildPsiCore($"\"{dllPath}\"{TestBuildConfig.BcVersionArg} \"{Fixture}\"");

    /// <summary>
    /// Same as <see cref="BuildPsi"/>, but WITHOUT the pinned `--bc-version` argument —
    /// needed by issue #2097's test, which is specifically about the auto-selection
    /// "[bc] no --bc-version given — selecting BC ..." line that only prints when no
    /// version is given on the command line at all.
    /// </summary>
    private ProcessStartInfo BuildPsiWithoutBcVersion(string dllPath) =>
        BuildPsiCore($"\"{dllPath}\" \"{Fixture}\"");

    private ProcessStartInfo BuildPsiCore(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Deliberately default verbosity — no AL_RUNNER_VERBOSE — this test is about
        // what a real user sees by default, and #2038 already made every line asserted
        // on here (`[provision]`, `[bc]`, `[reexec]`) survive that filter.
        psi.Environment.Remove("AL_RUNNER_VERBOSE");
        psi.Environment.Remove("AL_RUNNER_NCL_SHADOW_DONE");
        psi.Environment.Remove("AL_RUNNER_REEXECED");
        return psi;
    }

    private static (string Output, int Exit) Run(ProcessStartInfo psi)
    {
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(300_000), "runner did not exit within 300s");
        p.WaitForExit();
        return (sb.ToString(), p.ExitCode);
    }
}
