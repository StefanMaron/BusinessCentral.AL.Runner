// Issue #2223: once the runner-owned `<artifacts>/<ver>/platform-apps` directory exists, the
// #2067 extraProvisionSearchDirs block folds it into packageCacheDirs UNCONDITIONALLY, so every
// invocation resolves and loads the Base Application / System Application closure — including
// the bundle shape #2232 already established does not need it.
//
// The sharpest consequence is that #2232's own deferral stops working as designed. Its env var
// (AL_RUNNER_DEFERRED_PLATFORM_APPS) suppresses only the DOWNLOAD decision, never the fold, so
// the "attempt without the platform apps" ran WITH them whenever they happened to be on disk —
// the one process in the codebase whose entire purpose is to answer "does this bundle work
// without the closure?" could not ask the question.
//
// These tests assert the OBSERVABLE — which dependencies the run resolves — never a wall clock.
// A timing assertion would be flaky by construction and would not say which mechanism moved.
//
// The fixtures declare only a `platform` floor, never `application`
// (.claude/rules/no-base-app-in-csharp-tests.md). A platform floor alone synthesises the
// Microsoft/System implicit root, which is enough to reach the fold this pins.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class DeferredPlatformAppsWithholdTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    /// <summary>A `[dep] Microsoft/...` verbose line names one resolved platform package.</summary>
    private const string PlatformDepMarker = "[dep] Microsoft/";

    private static string RealServiceTierDir()
    {
        var version = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion()
            ?? throw new InvalidOperationException("EngineBuiltVersion() unavailable.");
        var home = Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException("HOME not set on this machine.");
        return Path.Combine(TestArtifacts.StandardCacheDir(home), version.ToString());
    }

    /// <summary>
    /// A bundle with a real `platform` floor whose AL names nothing Microsoft — #2232's shape,
    /// and the one this issue measures.
    /// </summary>
    private static string WriteBundle(string dir, int id)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{Guid.NewGuid()}}",
          "name": "Withhold {{id}}",
          "publisher": "Repro2223",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": {{id}}, "to": {{id + 9}} } ],
          "platform": "27.0.0.0",
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "Tests.Codeunit.al"), $$"""
        table {{id}} "Wh Row {{id}}"
        {
            fields { field(1; "No."; Integer) { } field(2; Txt; Text[30]) { } }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        codeunit {{id + 1}} "Wh Tests {{id}}"
        {
            Subtype = Test;

            [Test]
            procedure TheTest()
            var
                Row: Record "Wh Row {{id}}";
            begin
                Row."No." := 7; Row.Txt := 'seven'; Row.Insert(true);
                Row.Get(7);
                if Row.Txt <> 'seven' then
                    Error('expected seven, got %1', Row.Txt);
            end;
        }
        """);
        return dir;
    }

    /// <summary>
    /// Builds an artifacts root holding a REAL, COMPLETE runner-owned platform-apps directory for
    /// the engine's own version, by symlinking the machine's provisioned copy. That presence is
    /// the whole subject: it is what makes the fold resolve the closure.
    /// </summary>
    private static string? BuildArtifactsRootWithPlatformApps(string scratchRoot)
    {
        var version = AlRunner.Infrastructure.BcArtifacts.EngineBuiltVersion();
        if (version == null) return null;
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home)) return null;

        var realVersionDir = Path.Combine(TestArtifacts.StandardCacheDir(home), version.ToString());
        var realPlatformApps = Path.Combine(realVersionDir, "platform-apps");
        if (!Directory.Exists(realPlatformApps)) return null;
        // A partially provisioned directory would make the run refuse for a DIFFERENT reason
        // (a missing app), which is not this issue's shape and would read as a pass here.
        if (!Directory.EnumerateFiles(realPlatformApps, "*.app").Any()) return null;

        var root = Path.Combine(scratchRoot, "artifacts");
        var versionDir = Path.Combine(root, version.ToString());
        Directory.CreateDirectory(versionDir);
        Directory.CreateSymbolicLink(Path.Combine(versionDir, "platform-apps"), realPlatformApps);
        return root;
    }

    private static (string Output, int Exit) RunDeferredChild(
        string bundleDir, string scratchRoot, string artifactsRoot)
    {
        var realServiceTierDir = RealServiceTierDir();
        TestArtifacts.SkipIf(!Directory.Exists(realServiceTierDir),
            $"real BC service-tier dir not provisioned at '{realServiceTierDir}'.");
        var isolatedHome = Path.Combine(scratchRoot, "home");
        Directory.CreateDirectory(isolatedHome);

        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append($" --artifact-path \"{realServiceTierDir}\"");
        args.Append($" \"{bundleDir}\"");
        args.Append($" --cache \"{Path.Combine(scratchRoot, "al-out")}\"");
        // Never created: the ONLY platform-apps the run can see is the runner-owned directory
        // under the artifacts root below, which is exactly the fold under test.
        args.Append($" --package-cache \"{Path.Combine(isolatedHome, "no-such-package-cache")}\"");
        args.Append(" --no-auto-provision --verbose");

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
        psi.Environment["HOME"] = isolatedHome;
        psi.Environment["AL_RUNNER_ARTIFACTS_ROOT"] = artifactsRoot;
        // This process IS the attempt without the platform apps — the state #2232's parent puts
        // its child in. Setting it directly keeps the test to one process and one question.
        psi.Environment[AlRunner.Infrastructure.ProvisioningCheck.DeferredPlatformAppsEnvVar] = "1";

        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static void WithScratch(string name, Action<string> body)
    {
        var scratchRoot = TestScratch.Dir(name);
        try { body(scratchRoot); }
        finally { try { Directory.Delete(scratchRoot, recursive: true); } catch { } }
    }

    private static IReadOnlyList<string> PlatformDepLines(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(PlatformDepMarker, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// The defect. #2232's attempt-without-the-platform-apps child, with a COMPLETE runner-owned
    /// platform-apps directory on disk, must resolve no Microsoft platform package at all —
    /// otherwise the attempt is not "without" them and its green verdict means nothing, while
    /// every ordinary invocation pays the same closure load this issue measures.
    ///
    /// Asserted on the resolved dependency list, which is the mechanism. A wall-clock assertion
    /// would pass or fail on machine load and would not name what moved.
    /// </summary>
    [SkippableFact]
    public void DeferredAttempt_WithPlatformAppsOnDisk_ResolvesNoMicrosoftPlatformPackage()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2223-withhold", scratch =>
        {
            var artifactsRoot = BuildArtifactsRootWithPlatformApps(scratch);
            TestArtifacts.SkipIf(artifactsRoot == null,
                "no complete runner-owned platform-apps directory is provisioned for the engine version; "
                + "this test's whole subject is the fold of that directory, so there is nothing to measure.");

            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61990);
            var (output, exit) = RunDeferredChild(bundle, scratch, artifactsRoot!);

            var platformDeps = PlatformDepLines(output);
            Assert.True(platformDeps.Count == 0,
                "the deferred attempt resolved Microsoft platform package(s) from the runner-owned "
                + "platform-apps directory, so it did not run WITHOUT them (#2223):\n  "
                + string.Join("\n  ", platformDeps) + "\n--- full output ---\n" + output);
            Assert.True(exit == 0, $"the attempt must still come out green. exit={exit}\n{output}");
            Assert.Contains("pass:        1", output);
        });
    }

    /// <summary>
    /// The control, and the half that keeps the fix from being a correctness bug. An ORDINARY
    /// invocation — no deferral env var — with the same platform-apps directory on disk must
    /// still resolve the closure. Without this, "withhold the directory" could be implemented as
    /// "never fold it", which would break every bundle that genuinely uses Base Application.
    /// </summary>
    [SkippableFact]
    public void OrdinaryRun_WithPlatformAppsOnDisk_StillResolvesTheMicrosoftClosure()
    {
        TestArtifacts.SkipIfMissing();
        WithScratch("al-runner-2223-control", scratch =>
        {
            var artifactsRoot = BuildArtifactsRootWithPlatformApps(scratch);
            TestArtifacts.SkipIf(artifactsRoot == null,
                "no complete runner-owned platform-apps directory is provisioned for the engine version.");

            var bundle = WriteBundle(Path.Combine(scratch, "bundle"), 61992);

            var realServiceTierDir = RealServiceTierDir();
            TestArtifacts.SkipIf(!Directory.Exists(realServiceTierDir),
                $"real BC service-tier dir not provisioned at '{realServiceTierDir}'.");
            var isolatedHome = Path.Combine(scratch, "home-control");
            Directory.CreateDirectory(isolatedHome);

            var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
            args.Append($" --artifact-path \"{realServiceTierDir}\"");
            args.Append($" \"{bundle}\"");
            args.Append($" --cache \"{Path.Combine(scratch, "al-out-control")}\"");
            args.Append($" --package-cache \"{Path.Combine(isolatedHome, "no-such-package-cache")}\"");
            args.Append(" --no-auto-provision --verbose");

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
            psi.Environment["HOME"] = isolatedHome;
            psi.Environment["AL_RUNNER_ARTIFACTS_ROOT"] = artifactsRoot!;
            psi.Environment.Remove(AlRunner.Infrastructure.ProvisioningCheck.DeferredPlatformAppsEnvVar);

            var sb = new StringBuilder();
            using var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
            p.WaitForExit();
            string output; lock (sb) output = sb.ToString();

            var platformDeps = PlatformDepLines(output);
            Assert.True(platformDeps.Count > 0,
                "an ordinary invocation must still resolve the Microsoft platform closure from the "
                + "runner-owned platform-apps directory; withholding it from every run would break "
                + "bundles that genuinely use Base Application (#2223).\n--- full output ---\n" + output);
        });
    }
}
