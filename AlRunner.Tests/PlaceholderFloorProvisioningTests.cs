// Issue #2229 — a placeholder application/platform floor is not a real Microsoft
// dependency, on a genuinely cold platform-apps cache.
//
// #2205 (PR #2220) made the two implicit `Application`/`System` roots ReadDependencies
// synthesises from app.json's `application`/`platform` fields ALWAYS count as a real
// platform-app requirement — the right call for the shape it measured
// (tests/runner-extras/microsoft-dependencies, which genuinely resolves Base/System
// Application record names). `application`/`platform` are synthesised on EVERY AL
// bundle regardless of whether its code references anything Microsoft, because that is
// how `al` derives them.
//
// A FIRST attempt at this issue tried to distinguish "no real requirement" from "a real
// requirement" by the FLOOR VALUE alone (treating a `1.0.0.0` placeholder as never a
// genuine need). That is wrong, and was caught by measurement, not review: a `1.0.0.0`
// floor and genuine Base/System Application usage are not mutually exclusive — an
// ordinary app can declare no real floor AND still resolve a System Application
// codeunit by name. Reproduced directly: `"dependencies": []`, `application`/`platform`
// of `1.0.0.0`, one test declaring `Codeunit "Environment Information"` — regressed from
// `2P/0F/0E` on main to `EMIT-ZERO`/AL0185 "Codeunit 'Environment Information' is
// missing" under the floor-value heuristic, because the manifest alone cannot see what
// the AL source references — it looks IDENTICAL for "never touches Microsoft" and
// "touches Microsoft and set no real floor". #2232 (filed against the mirror shape —
// a REAL floor with no actual usage) already reaches the same conclusion: separating
// "declared" from "actually used" needs a compile attempt, not a version sentinel.
//
// The actual fix for #2229 is therefore NOT a product-behavior change here — it is
// giving the affected AlRunner.Tests fixtures no application/platform floor, so they
// declare what they actually need (nothing). See the individual fixture app.json diffs.
// This file exists purely as the regression guard: both directions of the platform-app
// need detection must survive completely untouched.
//
// Both tests run against the real runner subprocess, hermetically: --artifact-path pins
// the real, already-provisioned engine (no network needed for the ENGINE), while an
// isolated $HOME + a --package-cache pointed at a nonexistent directory make the
// platform-apps decision see a genuinely cold cache, without ever touching this
// machine's real ~/.local/share/al-runner or ~/.al-runner.
//
//   - PlaceholderFloorWithGenuineMicrosoftUsage_ColdCache_NoAutoProvision_StillRefuses:
//     the coordinator's exact repro. A placeholder 1.0.0.0 floor that DOES resolve a
//     System Application codeunit must still be detected and refused loudly (or
//     provisioned, with --auto-provision) — never silently reach AL0185 with no
//     explanation. This is what the abandoned floor-value heuristic broke.
//
//   - MicrosoftDependenciesFixture_ColdCache_NoAutoProvision_StillRefuses: the regression
//     guard for #2205 itself. Reuses tests/runner-extras/microsoft-dependencies (a REAL
//     27.0.0.0 floor, genuinely resolves Base/System Application record names) and
//     proves the loud, actionable refusal #2205 added is untouched.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// The "application" floor in this file's fixtures is DELIBERATE and must stay.
// Every other fixture in AlRunner.Tests dropped it (#2358) because it pulls in the whole
// Base Application closure for nothing -- ~70s cold / ~6s warm per runner invocation.
// Here the placeholder floor IS the subject of the test: remove it and there is nothing
// left being tested. This is not a violation of
// .claude/rules/no-base-app-in-csharp-tests.md; it is the case that rule carves out.
public sealed class PlaceholderFloorProvisioningTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static string RealServiceTierDir()
    {
        var version = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
            ?? throw new InvalidOperationException(
                "EngineBuiltVersion() unavailable — cannot locate a real artifact dir to pin --artifact-path at.");
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException("HOME not set on this machine.");
        return Path.Combine(TestArtifacts.StandardCacheDir(home), version.ToString());
    }

    /// <summary>
    /// The coordinator's exact reproduction: `"dependencies": []` plus a placeholder
    /// `1.0.0.0` application/platform floor, but a test that DOES resolve a real System
    /// Application codeunit by name ("Environment Information") — an ordinary shape,
    /// not a contrived one. Distinguishes this from
    /// AlRunner.Tests/Fixtures/EmitExclusion (same floor, but genuinely nothing
    /// Microsoft) purely by what the AL source references.
    /// </summary>
    private static string WritePlaceholderFloorWithMicrosoftUsageFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "Repro2229 Placeholder Floor Plus Microsoft Usage",
          "publisher": "Repro2229",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 61970, "to": 61979 } ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Repro2229Tests.al"), """
        codeunit 61970 "Repro2229 Tests"
        {
            Subtype = Test;

            [Test]
            procedure UsesEnvironmentInformation()
            var
                EnvironmentInfo: Codeunit "Environment Information";
            begin
                if EnvironmentInfo.IsSaaS() then
                    Error('unexpected');
            end;
        }
        """);
        return dir;
    }

    private static (string Output, int Exit) RunIsolated(string bundleDir, string isolatedHome, string alCacheDir)
    {
        var realServiceTierDir = RealServiceTierDir();
        TestArtifacts.SkipIf(!Directory.Exists(realServiceTierDir),
            $"real BC service-tier dir not provisioned at '{realServiceTierDir}' " +
            "(needed so --artifact-path can pin the engine while $HOME is isolated).");

        var absentPackageCache = Path.Combine(isolatedHome, "no-such-package-cache");
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append($" --artifact-path \"{realServiceTierDir}\"");
        args.Append($" \"{bundleDir}\"");
        args.Append($" --cache \"{alCacheDir}\"");
        // Deliberately never created — an isolated, genuinely cold platform-apps search
        // set, without touching this machine's real caches.
        args.Append($" --package-cache \"{absentPackageCache}\"");
        args.Append(" --no-auto-provision");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        // Isolates BcArtifacts.ArtifactsRootDir (and the runner-owned platform-apps/
        // test-apps fold-in candidates Program.cs derives from it) from this machine's
        // real provisioning history, WITHOUT touching BcArtifacts.ServiceTierDir — that
        // stays pinned at the literal --artifact-path above regardless of $HOME.
        psi.Environment["HOME"] = isolatedHome;

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        string output;
        lock (sb) output = sb.ToString();
        return (output, p.ExitCode);
    }

    [SkippableFact]
    public void PlaceholderFloorWithGenuineMicrosoftUsage_ColdCache_NoAutoProvision_StillRefuses()
    {
        TestArtifacts.SkipIfMissing();
        var scratchRoot = Path.Combine(
            Path.GetTempPath(), "al-runner-placeholder-floor-msuse", Guid.NewGuid().ToString("N"));
        var bundleDir = WritePlaceholderFloorWithMicrosoftUsageFixture(Path.Combine(scratchRoot, "bundle"));
        var isolatedHome = Path.Combine(scratchRoot, "home");
        Directory.CreateDirectory(isolatedHome);
        var alCacheDir = Path.Combine(scratchRoot, "al-out");
        try
        {
            var (output, exit) = RunIsolated(bundleDir, isolatedHome, alCacheDir);

            // The core claim: a placeholder 1.0.0.0 floor is NOT proof the bundle needs
            // nothing Microsoft — this one genuinely resolves a System Application
            // codeunit, and must be detected and refused loudly (not silently reach an
            // unexplained AL0185 "Codeunit ... is missing").
            Assert.True(exit == 2,
                $"a placeholder-floor bundle that genuinely resolves a System Application " +
                $"codeunit must still refuse loudly on a cold cache without " +
                $"--auto-provision. exit={exit}\n{output}");
            Assert.Contains("declares Microsoft dependencies", output);
            Assert.Contains("Application, System", output);
            Assert.DoesNotContain("AL0185", output);
        }
        finally
        {
            try { Directory.Delete(scratchRoot, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void MicrosoftDependenciesFixture_ColdCache_NoAutoProvision_StillRefuses()
    {
        TestArtifacts.SkipIfMissing();
        // tests/runner-extras/microsoft-dependencies: the #2205 shape this must not
        // regress — a REAL 27.0.0.0 floor, genuinely resolving Base/System Application
        // record names ("Payment Method", "No. Series", …).
        var bundleDir = Path.Combine(RepoRoot, "tests", "runner-extras", "microsoft-dependencies");
        TestArtifacts.SkipIf(!Directory.Exists(bundleDir), $"fixture not found: '{bundleDir}'.");

        var scratchRoot = Path.Combine(
            Path.GetTempPath(), "al-runner-genuine-msdep", Guid.NewGuid().ToString("N"));
        var isolatedHome = Path.Combine(scratchRoot, "home");
        Directory.CreateDirectory(isolatedHome);
        var alCacheDir = Path.Combine(scratchRoot, "al-out");
        try
        {
            var (output, exit) = RunIsolated(bundleDir, isolatedHome, alCacheDir);

            Assert.True(exit == 2,
                $"a bundle genuinely resolving Base/System Application record names must " +
                $"still refuse loudly on a cold cache without --auto-provision. exit={exit}\n{output}");
            Assert.Contains("declares Microsoft dependencies", output);
            Assert.Contains("Application, System", output);
        }
        finally
        {
            try { Directory.Delete(scratchRoot, recursive: true); } catch { }
        }
    }
}
