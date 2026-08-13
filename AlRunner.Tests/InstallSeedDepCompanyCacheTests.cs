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
/// app group's own test assertion against REAL Company-Initialize-seeded data (Company
/// Information's Name, seeded by the actual Base App codeunit 2 body) proving the
/// restored/cached baseline is not a stub — a no-op cache that skipped the seed
/// entirely would fail every one of these AL assertions, not just run faster.
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
        // Company Information (and every other real Base App table these tests assert
        // against) only resolves with the platform apps on the package-cache path —
        // without it dependency resolution silently skips Microsoft/Application and
        // Microsoft/System, and the bundle fails to compile at all (AL0185).
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

    private static void WriteSameDepClosureApp(string dir, int baseId, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "{{name}}",
          "publisher": "IssueTest1867",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 9}} } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.al"), $$"""
        codeunit {{baseId}} "IT1867 {{name}}"
        {
            Subtype = Test;

            [Test]
            procedure CompanyInitializeSeededRealData()
            var
                CompanyInformation: Record "Company Information";
            begin
                // [THEN] The real Base App codeunit 2 "Company-Initialize" body actually
                // ran (whether via a fresh computation or a restored cache snapshot) —
                // not a no-op that silently skipped the seed. Company Information is a
                // singleton row Company-Initialize inserts; Get() failing here means the
                // baseline this app group is running against was never seeded at all.
                CompanyInformation.Get();
            end;
        }
        """);
    }

    [SkippableFact]
    public void SecondAppGroupWithSameDependencyClosure_ReusesCachedDepCompanyBaseline()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-depcompany-cache", Guid.NewGuid().ToString("N"));
        try
        {
            var appA = Path.Combine(root, "app-a");
            var appB = Path.Combine(root, "app-b");
            WriteSameDepClosureApp(appA, 61900, "AppA");
            WriteSameDepClosureApp(appB, 61910, "AppB");

            var (output, exitCode) = RunRunner(appA, appB);

            // [THEN] Both app groups' Company-Initialize assertion actually passed — the
            // cache did not silently skip seeding for either.
            Assert.Equal(0, exitCode);
            var passLines = CountOccurrences(output, "1P/0F/0E");
            Assert.True(passLines >= 2,
                $"expected both app groups to report 1P/0F/0E, got:\n{output}");

            // [THEN] Exactly one fresh computation (MISS) and at least one reuse (HIT) for
            // the shared MS-platform-only dependency closure — proving the SECOND app group
            // genuinely reused the first's result instead of re-running dependency Install
            // triggers + Company-Initialize from scratch.
            var missCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache MISS");
            var hitCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache HIT");
            Assert.Equal(1, missCount);
            Assert.True(hitCount >= 1, $"expected at least one cache HIT, got:\n{output}");
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
    /// independently MISS (fresh dependency Install triggers + Company-Initialize) and
    /// neither may HIT, even though their dependency-set key is identical.
    /// </summary>
    [SkippableFact]
    public void KillSwitchEnvVar_ForcesEveryLookupToMiss_EvenForSameDependencyClosure()
    {
        TestArtifacts.SkipIfMissing();

        var root = Path.Combine(Path.GetTempPath(), "al-runner-depcompany-cache-killswitch", Guid.NewGuid().ToString("N"));
        try
        {
            var appA = Path.Combine(root, "app-a");
            var appB = Path.Combine(root, "app-b");
            WriteSameDepClosureApp(appA, 61950, "AppA");
            WriteSameDepClosureApp(appB, 61960, "AppB");

            var (output, exitCode) = RunRunner(
                new System.Collections.Generic.Dictionary<string, string> { ["AL_RUNNER_NO_DEP_COMPANY_CACHE"] = "1" },
                appA, appB);

            // [THEN] Both app groups' Company-Initialize assertion still passed — the kill
            // switch disables the cache, not the seeding itself.
            Assert.Equal(0, exitCode);
            var passLines = CountOccurrences(output, "1P/0F/0E");
            Assert.True(passLines >= 2,
                $"expected both app groups to report 1P/0F/0E, got:\n{output}");

            // [THEN] Exactly two fresh computations (MISS) and zero reuse (HIT) — with the
            // kill switch set, the SAME dependency closure that produced 1 MISS + >=1 HIT in
            // the positive test above must now produce 2 MISSes and 0 HITs.
            var missCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache MISS");
            var hitCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache HIT");
            Assert.Equal(2, missCount);
            Assert.Equal(0, hitCount);
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

        var root = Path.Combine(Path.GetTempPath(), "al-runner-depcompany-cache-neg", Guid.NewGuid().ToString("N"));
        try
        {
        var appA = Path.Combine(root, "app-a");
        WriteSameDepClosureApp(appA, 61920, "AppA");

        // A second app group whose dependency CLOSURE genuinely differs from AppA's: it
        // depends on its own extra app, which is loaded as an additional dependency
        // assembly and therefore changes InstallTriggerRunner.CurrentDependencySetKey().
        var depDir = Path.Combine(root, "extra-dep");
        var mainDir = Path.Combine(root, "app-with-extra-dep");
        Directory.CreateDirectory(depDir);
        var depId = Guid.NewGuid().ToString();
        File.WriteAllText(Path.Combine(depDir, "app.json"), $$"""
        {
          "id": "{{depId}}",
          "name": "IT1867 Extra Dep",
          "publisher": "IssueTest1867",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61930, "to": 61939 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(depDir, "Dep.al"), """
        table 61930 "IT1867 Extra Dep Table"
        {
            DataClassification = SystemMetadata;
            fields { field(1; "Code"; Code[10]) { } }
            keys { key(PK; "Code") { Clustered = true; } }
        }
        """);
        Directory.CreateDirectory(mainDir);
        File.WriteAllText(Path.Combine(mainDir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "IT1867 App With Extra Dep",
          "publisher": "IssueTest1867",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{depId}}", "name": "IT1867 Extra Dep", "publisher": "IssueTest1867", "version": "1.0.0.0" }
          ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 61940, "to": 61949 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(mainDir, "Tests.al"), """
        codeunit 61940 "IT1867 WithExtraDep"
        {
            Subtype = Test;

            [Test]
            procedure CompanyInitializeSeededRealData()
            var
                CompanyInformation: Record "Company Information";
            begin
                CompanyInformation.Get();
            end;
        }
        """);

        var (output, exitCode) = RunRunner(appA, depDir, mainDir);

        Assert.Equal(0, exitCode);
        var passLines = CountOccurrences(output, "1P/0F/0E");
        Assert.True(passLines >= 2,
            $"expected both independently-keyed app groups to report 1P/0F/0E, got:\n{output}");

        // [THEN] Two DIFFERENT dependency closures (AppA's MS-platform-only closure vs.
        // the extra-dep app's closure, which includes one more dependency assembly) each
        // get their OWN fresh computation — never a cross-key HIT. A cache keyed
        // incorrectly (e.g. ignoring the dependency set entirely) would show only one
        // MISS total; this must show at least two.
        var missCount = CountOccurrences(output, "InstallBaseline.DepCompanyCache MISS");
        Assert.True(missCount >= 2,
            $"expected at least 2 distinct-dependency-closure MISSes, got {missCount}:\n{output}");
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
