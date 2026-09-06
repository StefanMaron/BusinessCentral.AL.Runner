using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #1867 — InstallTriggerRunner.RunAll() (dependency Install triggers) plus
/// CompanyInitializer.EnsureCompanyInitialized() (real codeunit 2 "Company-Initialize")
/// together accounted for ~82.5% of runner-extras' per-app-group "install-seed" cost
/// (measured via #1866's AppStage breakdown), and were being re-run from scratch for
/// EVERY app group even though the ~12-assembly MS platform dependency closure — and
/// therefore the result of firing its Install triggers + Company-Initialize — is
/// identical across app groups that share that closure. TestExecutor.Run now caches the
/// resulting snapshot keyed by InstallTriggerRunner.CurrentDependencySetKey() (built from
/// each dependency assembly's Module Version ID) and restores it on a later app group
/// with the same key, instead of re-running the dependency triggers + Company-Initialize.
///
/// The claim under test is NOT "it's faster" — it's the two things that would make a
/// cache here unsafe: (1) a SECOND app group sharing the same dependency closure must
/// reuse the cached computation (HIT), not redo it — proving the optimisation actually
/// activates; and (2) an app group whose dependency closure DIFFERS must NOT reuse
/// another app group's cached baseline (MISS on both the first app group AND the
/// differently-keyed one) — proving the cache is correctly scoped and never crosses
/// dependency-set boundaries. Both directions are asserted from the
/// AL_RUNNER_PERF=1 "InstallBaseline.DepCompanyCache HIT/MISS" markers TestExecutor.Run
/// logs at the exact point the cache lookup happens (see TestExecutor.cs), plus each
/// app group's own test assertion reading the dependency's install-seeded rows back BY
/// VALUE, proving the restored/cached baseline is not a stub — a no-op cache that skipped
/// the seed entirely would fail every one of these AL assertions, not just run faster.
///
/// #2364: the two app groups used to share their closure by declaring NOTHING but the Base
/// Application floor, and asserted on Company Information's Company-Initialize-seeded row.
/// Neither half of that is what this suite claims. They now share a real dependency —
/// <see cref="InstallSeedClosure"/>'s seed app, one table and one OnInstallAppPerCompany
/// trigger — so the shared key comes from the apps genuinely sharing a dependency assembly
/// rather than from both declaring none, and the AL assertion is about rows this fixture
/// seeded rather than rows Base App did. See .claude/rules/no-base-app-in-csharp-tests.md.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class InstallSeedDepCompanyCacheTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static (string output, int exit) RunRunner(params string[] bundles)
        => RunRunner(extraEnv: null, bundles);

    private static (string output, int exit) RunRunner(
        System.Collections.Generic.IDictionary<string, string>? extraEnv, params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        // The platform apps have to be on the package-cache path or dependency resolution
        // silently skips Microsoft/System and the bundle fails to compile at all (AL0185).
        // The bundles no longer declare the Base Application floor (#2364), but they still
        // need the platform closure they compile against.
        args.Append(" --package-cache \"").Append(TestArtifacts.PlatformAppsDir()).Append('"');
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            Environment = { ["AL_RUNNER_PERF"] = "1" },
        };
        if (extraEnv != null)
            foreach (var (k, v) in extraEnv) psi.Environment[k] = v;
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    [SkippableFact]
    public void SecondAppGroupWithSameDependencyClosure_ReusesCachedDepCompanyBaseline()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-depcompany-cache");
        try
        {
            var (appA, appB, _) = InstallSeedClosure.WriteSharedClosure(root, "hit", 61900);

            var (output, exitCode) = RunRunner(appA, appB);

            // [THEN] Both app groups' AL assertion actually passed — each read the seed
            // app's two install-seeded rows back by value, so the cache did not silently skip
            // seeding for either.
            Assert.Equal(0, exitCode);
            var passLines = CountOccurrences(output, "1P/0F/0E");
            Assert.True(passLines >= 2,
                $"expected both app groups to report 1P/0F/0E, got:\n{output}");

            // [THEN] The shared dependency closure was resolved exactly ONCE in this process —
            // and the second app group reused that result rather than re-running the
            // dependency's Install triggers from scratch.
            //
            // Both halves are exact rather than ">= 1" because #2364 made the closure unique
            // per invocation: the seed app's id and seeded marker carry a fresh GUID, so its
            // assembly has an MVID no earlier run produced and the on-disk tier is guaranteed
            // not to hold an entry for this key. Before that the two app groups shared only
            // the MS platform closure, whose entry any earlier invocation on the machine may
            // already have written, so the first lookup could legitimately answer MISS or
            // DISK-HIT and only their SUM could be asserted.
            var missCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache MISS");
            var diskHitCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache DISK-HIT");
            var hitCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache HIT");
            Assert.Equal(1, missCount);
            Assert.Equal(0, diskHitCount);
            Assert.Equal(1, hitCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// Clean inversion of <see cref="SecondAppGroupWithSameDependencyClosure_ReusesCachedDepCompanyBaseline"/>:
    /// same two-app-group, same-dependency-closure scenario, but with the permanent kill
    /// switch (AL_RUNNER_NO_DEP_COMPANY_CACHE=1) set on the spawned process. Proves the
    /// switch actually disables reuse rather than merely existing — both app groups must
    /// independently MISS (fresh dependency Install triggers) and neither may HIT, even
    /// though their dependency-set key is identical.
    /// </summary>
    [SkippableFact]
    public void KillSwitchEnvVar_ForcesEveryLookupToMiss_EvenForSameDependencyClosure()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-depcompany-cache-killswitch");
        try
        {
            var (appA, appB, _) = InstallSeedClosure.WriteSharedClosure(root, "ks", 61950);

            var (output, exitCode) = RunRunner(
                new System.Collections.Generic.Dictionary<string, string> { ["AL_RUNNER_NO_DEP_COMPANY_CACHE"] = "1" },
                appA, appB);

            // [THEN] Both app groups' AL assertion still passed — the kill switch disables
            // the cache, not the seeding itself.
            Assert.Equal(0, exitCode);
            var passLines = CountOccurrences(output, "1P/0F/0E");
            Assert.True(passLines >= 2,
                $"expected both app groups to report 1P/0F/0E, got:\n{output}");

            // [THEN] Exactly two fresh computations (MISS) and zero reuse of any kind — with
            // the kill switch set, the SAME dependency closure that produced 1 resolution +
            // >=1 HIT in the positive test above must now produce 2 MISSes, 0 in-memory HITs
            // and 0 DISK-HITs. The last of those is the switch's cross-process half: it must
            // bypass the on-disk tier too, or a run set up to re-measure the uncached path
            // would silently keep reading yesterday's answer.
            var missCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache MISS");
            var hitCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache HIT");
            var diskHitCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache DISK-HIT");
            Assert.Equal(2, missCount);
            Assert.Equal(0, hitCount);
            Assert.Equal(0, diskHitCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [SkippableFact]
    public void AppGroupWithOwnDependencyApp_DoesNotReuseUnrelatedDependencyClosureCache()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-depcompany-cache-neg");
        try
        {
        // appA's closure is the shared seed app alone. The second bundle declares the SAME
        // seed PLUS one extra dependency app, so its dependency closure differs from appA's by
        // exactly one loaded assembly and therefore by
        // InstallTriggerRunner.CurrentDependencySetKey(). Both still seed rows, so both are
        // genuinely persistable — a difference in KEY, not a difference in whether there is
        // anything to cache.
        var (appA, _, _) = InstallSeedClosure.WriteSharedClosure(root, "neg", 61920);
        var withExtra = InstallSeedClosure.WriteBundleWithExtraDependency(root, "x", 61930, "neg");

        var (output, exitCode) = RunRunner(appA, withExtra);

        Assert.Equal(0, exitCode);
        var passLines = CountOccurrences(output, "1P/0F/0E");
        Assert.True(passLines >= 2,
            $"expected both independently-keyed app groups to report 1P/0F/0E, got:\n{output}");

        // [THEN] Two DIFFERENT dependency closures (the seed app alone vs. the seed app plus
        // one more dependency assembly) each get their OWN resolution — never a cross-key
        // in-memory HIT. A cache keyed so that these two closures collide would show one
        // resolution and one HIT; this shows two resolutions and no HIT.
        //
        // WHAT THIS DOES AND DOES NOT PIN, measured by mutation (#2364; gap tracked in #3254):
        // TestExecutor.CurrentInstallBaselineCacheKey() concatenates three components, and the
        // first two — InstallTriggerRunner.CurrentDependencySetKey() and
        // RecordPatches.RegisteredBcAppSymbolStateKey() — are REDUNDANT for this scenario:
        // flattening either one alone to a constant leaves this test (and every other test in
        // both install-cache suites) green, because the other still separates the two closures.
        // Flattening BOTH fails this test and
        // InstallBaselineDiskCacheTests.DifferentDependencyClosures_*. So the claim this
        // actually establishes is "the key separates these two closures", not "the DEPENDENCY
        // SET component does". An earlier version of this comment asserted the latter; it was
        // not true and no test had ever checked it.
        //
        // Exact, for the same reason as the positive test above: both closures are unique to
        // this invocation, so neither can be answered by the on-disk tier.
        var missCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache MISS");
        var diskHitCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache DISK-HIT");
        var hitCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache HIT");
        Assert.Equal(2, missCount);
        Assert.Equal(0, diskHitCount);
        Assert.Equal(0, hitCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
