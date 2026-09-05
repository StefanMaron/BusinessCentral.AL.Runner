// ArtifactsRootEnvOverrideTests — issue #2578.
//
// BcArtifacts.ArtifactsRoot was Path.Combine(AlRunnerPaths.UserHome, ArtifactsRoot_Rel),
// so the ONLY way to move the BC artifact cache off the home directory was to move $HOME
// itself — which relocates every other home-rooted path (~/.cache/al-runner,
// ~/.bcartifacts.cache, ~/.local/share/al-runner/symbols) at the same time and forces the
// caller to hand-spell the version-directory layout BcArtifacts owns. AL_RUNNER_ARTIFACTS_ROOT
// names that root directly.
//
// Not the same knob as --artifact-path: that pins ONE version's engine directory (and
// bypasses version selection entirely); this names the root those version directories live
// under, so --bc-version / latest-in-cache selection and provisioning keep working.
//
// Per .claude/rules/bc-behavior-tests-go-upstream.md this is a runner-configuration claim
// (where the CLI and the BUILD resolve their own artifact cache) and asserts nothing about
// Business Central, so it belongs here and owes no al-language corpus test.
//
// Three layers, because a variable honoured by only one of them is a half-feature:
//   1. the pure resolver (absolutization, trailing-separator canonicalization, fallback),
//   2. the real CLI, spawned as a subprocess so nothing in-process mutates the shared
//      environment other parallel test classes read,
//   3. the MSBuild evaluation of both csprojs that <Reference> the service-tier DLLs —
//      AlRunner.csproj hard-ERRORS (FailIfBCServiceTierDllsMissing) when they are absent,
//      so a runtime-only override would leave the build unable to find a relocated cache.

using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class ArtifactsRootEnvOverrideTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // Never on the CDN and never in a cache: the process must fail at artifact-root
    // resolution, long before "does this version exist" is asked (same reason
    // HomeDirectoryMissingLoudFailureTests uses 1.2.3.4).
    private const string NonexistentVersion = "1.2.3.4";

    private static string Norm(string p) => p.Replace('\\', '/');

    // ---------------------------------------------------------------- 1. pure resolver

    [Fact]
    public void NoOverride_FallsBackToTheHomeRootedDefault()
    {
        var resolved = BcArtifacts.ResolveArtifactsRoot(null, () => "/home/someone");

        Assert.Equal("/home/someone/.local/share/al-runner/artifacts", Norm(resolved));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOverride_FallsBackToTheHomeRootedDefault(string blank)
    {
        // `export AL_RUNNER_ARTIFACTS_ROOT=` in a shell profile must not silently route the
        // whole cache at the current working directory.
        var resolved = BcArtifacts.ResolveArtifactsRoot(blank, () => "/home/someone");

        Assert.Equal("/home/someone/.local/share/al-runner/artifacts", Norm(resolved));
    }

    [Fact]
    public void AbsoluteOverride_IsUsedVerbatim_AndTheHomeDirectoryIsNeverConsulted()
    {
        var custom = Path.Combine(Path.GetTempPath(), "al-runner-2578-abs");

        // The home provider throws: this resolution must not be coupled to a $HOME resolution
        // that can fail for an unrelated reason (#2114). Scoped to the resolver — Program.cs's
        // AL-output cache resolves UserHome independently, so a broken $HOME still ends the
        // run; what this pins is that the override itself never depends on it.
        var resolved = BcArtifacts.ResolveArtifactsRoot(
            custom, () => throw new InvalidOperationException("UserHome must not be consulted"));

        Assert.Equal(Norm(custom), Norm(resolved));
        Assert.DoesNotContain(".local/share/al-runner", Norm(resolved));
    }

    [Fact]
    public void RelativeOverride_IsAbsolutized_ResolvingTheSameWayAnAbsoluteValueDoes()
    {
        // A relative root never matches TryTranslateArtifactPathToVersion's ordinal compare
        // against Path.Combine(ArtifactsRoot, version), so it would silently route every
        // --artifact-path onto the explicit-root branch instead of normal version selection.
        var resolved = BcArtifacts.ResolveArtifactsRoot("rel-arts", () => "/home/someone");

        Assert.True(Path.IsPathRooted(resolved), $"expected a rooted path, got '{resolved}'");
        Assert.Equal(Norm(Path.GetFullPath("rel-arts")), Norm(resolved));

        // "Resolves the same way an absolute one does": the absolute spelling of the same
        // directory produces the identical string.
        Assert.Equal(
            Norm(BcArtifacts.ResolveArtifactsRoot(Path.GetFullPath("rel-arts"), () => "/home/someone")),
            Norm(resolved));
    }

    [Fact]
    public void TrailingSeparatorIsTrimmed_SoTheArtifactPathOrdinalCompareStillMatches()
    {
        var custom = Path.Combine(Path.GetTempPath(), "al-runner-2578-trail");

        var resolved = BcArtifacts.ResolveArtifactsRoot(
            custom + Path.DirectorySeparatorChar, () => "/home/someone");

        Assert.Equal(Norm(custom), Norm(resolved));
    }

    [Fact]
    public void ArtifactDirForAndArtifactsRootDir_AgreeWithTheResolver()
    {
        // Both public entry points must be derived from the same resolution, not from a
        // second hand-spelled Path.Combine(UserHome, ...) that would ignore the override.
        var root = BcArtifacts.ArtifactsRootDir;

        Assert.Equal(
            Norm(Path.Combine(root, "28.1.49838.53910")),
            Norm(BcArtifacts.ArtifactDirFor("28.1.49838.53910")));
    }

    // ------------------------------------------------------- 2. the real CLI, subprocess

    private static (int ExitCode, string StdErr) RunCli(
        string? artifactsRootEnv, string? homeOverride = null, string? workingDir = null)
        => RunRunner(artifactsRootEnv, homeOverride, workingDir,
                     $"--bc-version {NonexistentVersion} --no-auto-provision");

    private static (int ExitCode, string Output) RunRunner(
        string? artifactsRootEnv, string? homeOverride, string? workingDir, string runnerArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(' ').Append(runnerArgs);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? RepoRoot,
        };
        // Set on the CHILD only. Mutating this process's environment would leak into every
        // other test class spawning a runner concurrently (maxParallelThreads = 4).
        if (artifactsRootEnv == null) psi.Environment.Remove("AL_RUNNER_ARTIFACTS_ROOT");
        else psi.Environment["AL_RUNNER_ARTIFACTS_ROOT"] = artifactsRootEnv;
        if (homeOverride != null)
        {
            psi.Environment["HOME"] = homeOverride;
            psi.Environment["USERPROFILE"] = homeOverride; // SpecialFolder.UserProfile on Windows
        }

        // Both pipes into one buffer, drained concurrently: the runner writes its
        // "[bc] selected BC <ver> (<dir>)" line and its diagnostics to stderr and its run
        // summary to stdout, and reading one to the end before the other deadlocks once the
        // 64K buffer of the unread pipe fills (see CliDocumentationTests' note).
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("al-runner did not exit within 120s.");
        }
        proc.WaitForExit();
        lock (errSb) return (proc.ExitCode, errSb.ToString());
    }

    [Fact]
    public void Cli_WithTheEnvVarSet_ResolvesUnderThatRoot_AndNotUnderHome()
    {
        // An isolated (existing, empty) home, so "did it use the override" is decidable:
        // the diagnostic must name the override and must NOT name this home's own tree.
        var isolatedHome = TestScratch.Dir("al-runner-2578-home");
        Directory.CreateDirectory(isolatedHome);
        var custom = TestScratch.Dir("al-runner-2578-root"); // deliberately never created
        try
        {
            var (exit, stderr) = RunCli(custom, isolatedHome);

            Assert.Equal(2, exit);
            Assert.Contains($"BC artifact root not found: {custom}", Norm(stderr));
            Assert.DoesNotContain(Norm(TestArtifacts.StandardCacheDir(isolatedHome)), Norm(stderr));
        }
        finally { try { Directory.Delete(isolatedHome, recursive: true); } catch { } }
    }

    [Fact]
    public void Cli_WithoutTheEnvVar_StillUsesTheHomeRootedDefault()
    {
        // The regression direction: a fix that broke this would break every existing box.
        var isolatedHome = TestScratch.Dir("al-runner-2578-home-default");
        Directory.CreateDirectory(isolatedHome);
        try
        {
            var (exit, stderr) = RunCli(artifactsRootEnv: null, homeOverride: isolatedHome);

            Assert.Equal(2, exit);
            Assert.Contains(
                $"BC artifact root not found: {Norm(TestArtifacts.StandardCacheDir(isolatedHome))}",
                Norm(stderr));
        }
        finally { try { Directory.Delete(isolatedHome, recursive: true); } catch { } }
    }

    [Fact]
    public void Cli_WithARelativeEnvValue_ReportsAnAbsolutePath()
    {
        var isolatedHome = TestScratch.Dir("al-runner-2578-home-rel");
        Directory.CreateDirectory(isolatedHome);
        var cwd = TestScratch.Dir("al-runner-2578-cwd");
        Directory.CreateDirectory(cwd);
        try
        {
            var (exit, stderr) = RunCli("rel-arts", isolatedHome, workingDir: cwd);

            Assert.Equal(2, exit);
            // Absolutized against the child's own working directory — never reported (or
            // probed) as the bare relative string.
            Assert.Contains($"BC artifact root not found: {Norm(Path.Combine(cwd, "rel-arts"))}",
                            Norm(stderr));
            Assert.DoesNotContain("BC artifact root not found: rel-arts", Norm(stderr));
        }
        finally
        {
            try { Directory.Delete(isolatedHome, recursive: true); } catch { }
            try { Directory.Delete(cwd, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The positive end-to-end half: a real run, real BC engine, tests actually executing —
    /// out of a root that is NOT the home-rooted default. The three cases above prove the
    /// resolution by reading a failure message; this one proves version SELECTION and the
    /// default package-cache/provisioning probes (both derived from ArtifactsRootDir) really
    /// follow the variable, which a message-only assertion cannot show.
    ///
    /// The relocated root is a SYMLINK to the provisioned one, so the test costs no copy of a
    /// multi-hundred-MB artifact tree. Path.GetFullPath does not resolve symlinks, so the
    /// runner's own selection line still names the relocated path — if it named the link
    /// target instead, that would mean the root came from somewhere other than the variable.
    /// </summary>
    [SkippableFact]
    public void Cli_RunsARealSuite_OutOfARelocatedArtifactsRoot()
    {
        TestArtifacts.SkipIfMissing();
        var provisioned = TestArtifacts.StandardCacheDir(TestArtifacts.HomeDir()!);
        // The gate is also satisfied by the legacy BcContainerHelper layout, which is not the
        // tree this test relocates — so require the runner-owned one specifically.
        TestArtifacts.SkipIf(!Directory.Exists(provisioned),
            $"'{provisioned}' is what this test relocates, and it is not present.");

        var relocatedParent = TestScratch.Dir("al-runner-2578-relocated");
        Directory.CreateDirectory(relocatedParent);
        var relocated = Path.Combine(relocatedParent, "artifacts");
        try
        {
            Directory.CreateSymbolicLink(relocated, provisioned);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows needs Developer Mode or elevation to create a directory symlink.
            throw new SkipException($"cannot create a directory symlink here: {ex.Message}");
        }

        try
        {
            // RecordTriggerXRec: the suite's most-spawned fixture, declares no Microsoft
            // dependency (see no-base-app-in-csharp-tests.md), so it needs nothing but the
            // engine — which is exactly what the relocated root has to supply.
            var (exit, output) = RunRunner(
                relocated, homeOverride: null, workingDir: null,
                runnerArgs: "--no-auto-provision \"" +
                            Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec") + "\"");

            Assert.True(exit == 0, $"a run out of the relocated root must succeed. exit={exit}\n{output}");

            // The engine was loaded from the relocated root, named by the runner itself.
            Assert.Contains($"({Norm(relocated)}/", Norm(output));
            Assert.Contains("selected BC", output, StringComparison.Ordinal);

            // And tests really ran — a run that selected the right root but executed nothing
            // would otherwise satisfy every assertion above.
            Assert.Contains("1P/0F/0E", output, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(relocatedParent, recursive: true); } catch { } }
    }

    // -------------------------------------------------------------- 3. the build (MSBuild)

    private static string EvaluateServiceTierPath(string projectRelPath, string? artifactsRootEnv)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"msbuild \"{Path.Combine(RepoRoot, projectRelPath)}\" " +
                        "-getProperty:ServiceTierPath -nologo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        if (artifactsRootEnv == null) psi.Environment.Remove("AL_RUNNER_ARTIFACTS_ROOT");
        else psi.Environment["AL_RUNNER_ARTIFACTS_ROOT"] = artifactsRootEnv;

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("dotnet msbuild -getProperty did not finish within 120s.");
        }
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, $"msbuild evaluation failed ({proc.ExitCode}):\n{stdout}\n{stderr}");
        return stdout.Trim();
    }

    [Theory]
    [InlineData("AlRunner/AlRunner.csproj")]
    [InlineData("AlRunner.Tests/AlRunner.Tests.csproj")]
    public void Build_WithTheEnvVarSet_ReferencesTheServiceTierDllsUnderThatRoot(string projectRelPath)
    {
        // AlRunner.csproj's FailIfBCServiceTierDllsMissing target hard-errors when the
        // service-tier DLLs are absent from ServiceTierPath, so a runtime-only override
        // would leave a relocated cache unbuildable on a box with nothing under $HOME.
        var custom = Path.Combine(Path.GetTempPath(), "al-runner-2578-build-root");

        var resolved = Norm(EvaluateServiceTierPath(projectRelPath, custom));

        Assert.StartsWith(Norm(custom) + "/", resolved);
        Assert.DoesNotContain(".local/share/al-runner", resolved);
    }

    [Theory]
    [InlineData("AlRunner/AlRunner.csproj")]
    [InlineData("AlRunner.Tests/AlRunner.Tests.csproj")]
    public void Build_WithoutTheEnvVar_KeepsTheHomeRootedDefault(string projectRelPath)
    {
        var resolved = Norm(EvaluateServiceTierPath(projectRelPath, artifactsRootEnv: null));

        Assert.Contains(".local/share/al-runner/artifacts/", resolved);
    }
}
