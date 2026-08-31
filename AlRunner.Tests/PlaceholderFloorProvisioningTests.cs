// Issue #2229 — a placeholder application/platform floor is not a real Microsoft
// dependency, on a genuinely cold platform-apps cache.
//
// #2205 (PR #2220) made the two implicit `Application`/`System` roots ReadDependencies
// synthesises from app.json's `application`/`platform` fields ALWAYS count as a real
// platform-app requirement — the right call for the shape it measured
// (tests/runner-extras/microsoft-dependencies, which genuinely resolves Base/System
// Application record names). But `application`/`platform` are synthesised on EVERY AL
// bundle regardless of whether its code references anything Microsoft, because that is
// how `al` derives them, not a statement of real need. A bundle with no real BC version
// to target writes the literal placeholder `1.0.0.0` — this repo's own
// AlRunner.Tests/Fixtures carries it on 14 fixtures, none of which reference a Microsoft
// type — and #2205's blanket rule turned that into a mandatory 116 MB download (or,
// offline / --no-auto-provision, an outright refusal) on every cold machine, for bundles
// that need nothing Microsoft at all.
//
// These two tests prove BOTH directions end-to-end, against the real runner subprocess,
// hermetically: --artifact-path pins the real, already-provisioned engine (no network
// needed for the ENGINE), while an isolated $HOME + a --package-cache pointed at a
// nonexistent directory make the platform-apps decision see a genuinely cold cache,
// without ever touching this machine's real ~/.local/share/al-runner or ~/.al-runner.
//
//   - PlaceholderFloor_ColdCache_NoAutoProvision_ProceedsWithoutRefusal: the fixture this
//     issue is about. Must NOT refuse or ask for 116 MB — proceeds straight to compile
//     and passes. A regression here (the network dependency "coming back") turns this
//     back into an exit-2 refusal for every placeholder-floor fixture in this repo.
//
//   - GenuineMicrosoftDependency_ColdCache_NoAutoProvision_StillRefuses: the regression
//     guard for #2205 itself. Reuses tests/runner-extras/microsoft-dependencies (a REAL
//     27.0.0.0 floor, genuinely resolves Base/System Application record names) and
//     proves the loud, actionable refusal #2205 added is untouched.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

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
    /// Exactly the shape issue #2229 is about: `"dependencies": []` plus a placeholder
    /// `1.0.0.0` application/platform floor, and AL source that references nothing
    /// Microsoft — mirrors AlRunner.Tests/Fixtures/EmitExclusion's own app.json.
    /// </summary>
    private static string WritePlaceholderFloorFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{id}}",
          "name": "Repro2229 Placeholder Floor",
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
            procedure NothingMicrosoftHere()
            begin
                if 1 + 1 <> 2 then
                    Error('arithmetic broke');
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
    public void PlaceholderFloor_ColdCache_NoAutoProvision_ProceedsWithoutRefusal()
    {
        TestArtifacts.SkipIfMissing();
        var scratchRoot = Path.Combine(
            Path.GetTempPath(), "al-runner-placeholder-floor", Guid.NewGuid().ToString("N"));
        var bundleDir = WritePlaceholderFloorFixture(Path.Combine(scratchRoot, "bundle"));
        var isolatedHome = Path.Combine(scratchRoot, "home");
        Directory.CreateDirectory(isolatedHome);
        var alCacheDir = Path.Combine(scratchRoot, "al-out");
        try
        {
            var (output, exit) = RunIsolated(bundleDir, isolatedHome, alCacheDir);

            // The core claim: a placeholder 1.0.0.0 floor with no Microsoft usage must
            // never be reported as a real requirement, on a completely cold platform-apps
            // cache. If this string reappears, the network dependency came back.
            Assert.DoesNotContain("declares Microsoft dependencies", output);
            Assert.True(exit == 0,
                $"a placeholder-floor bundle with no Microsoft usage must compile and pass " +
                $"on a cold cache, without --auto-provision, exit=0. exit={exit}\n{output}");
            Assert.Contains("1P/0F/0E", output);
        }
        finally
        {
            try { Directory.Delete(scratchRoot, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void GenuineMicrosoftDependency_ColdCache_NoAutoProvision_StillRefuses()
    {
        TestArtifacts.SkipIfMissing();
        // tests/runner-extras/microsoft-dependencies: the #2205 shape this fix must not
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
