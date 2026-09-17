// HomeRootedPathsEnvOverrideTests — issue #2768, the follow-up to #2578.
//
// #2578 added AL_RUNNER_ARTIFACTS_ROOT for the BC artifacts root. Its siblings had no knob:
//   - the curated symbols tree   (<home>/.local/share/al-runner/symbols) -> AL_RUNNER_SYMBOLS_ROOT
//   - the runner's own cache tree (<home>/.cache/al-runner)              -> AL_RUNNER_CACHE_ROOT
//   - the TestArtifacts gate, which probed only the home-rooted artifacts root.
//
// Same mechanism as ArtifactsRootEnvOverrideTests: a pure resolver per root, then the real CLI
// as a subprocess. No MSBuild layer — neither tree is a build input.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class HomeRootedPathsEnvOverrideTests
{
    /// <summary>The cap this file's subprocess spawns actually apply, and the single source of
    /// the figure their timeout messages report (#4275). Derived rather than repeated: a literal
    /// in the message is invisible while it happens to match, and wrong the moment the cap moves.
    /// Measured for real on #3435 — a cap squeezed to 3s still threw "did not exit within 120s".
    /// Same shape as BcVersionDefaultDocumentationTests.SpawnTimeoutMs (#3487).</summary>
    private const int SpawnTimeoutMs = 600_000;

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string Norm(string p) => p.Replace('\\', '/');

    // An absolute path that is never created: rooted at the current drive/root, not under TMPDIR.
    private static string AbsoluteFake(string name)
        => Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "al-runner-2768-never-created", name);

    private static readonly Func<string> HomeMustNotBeConsulted =
        () => throw new InvalidOperationException("UserHome must not be consulted");

    // ------------------------------------------------------------ symbols root resolver

    [Fact]
    public void SymbolsRoot_NoOverride_FallsBackToTheHomeRootedDefault()
    {
        Assert.Equal("/home/someone/.local/share/al-runner/symbols",
            Norm(BcArtifacts.ResolveSymbolsRoot(null, () => "/home/someone")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SymbolsRoot_BlankOverride_FallsBackToTheHomeRootedDefault(string blank)
    {
        Assert.Equal("/home/someone/.local/share/al-runner/symbols",
            Norm(BcArtifacts.ResolveSymbolsRoot(blank, () => "/home/someone")));
    }

    [Fact]
    public void SymbolsRoot_AbsoluteOverride_IsUsedVerbatim_WithoutConsultingHome()
    {
        var custom = AbsoluteFake("symbols");
        Assert.Equal(Norm(custom), Norm(BcArtifacts.ResolveSymbolsRoot(custom, HomeMustNotBeConsulted)));
    }

    [Fact]
    public void SymbolsRoot_RelativeOverride_IsAbsolutized_AndTrailingSeparatorTrimmed()
    {
        var resolved = BcArtifacts.ResolveSymbolsRoot("rel-syms" + Path.DirectorySeparatorChar, () => "/home/someone");
        Assert.True(Path.IsPathRooted(resolved), $"expected a rooted path, got '{resolved}'");
        Assert.Equal(Norm(Path.GetFullPath("rel-syms")), Norm(resolved));
    }

    [Fact]
    public void CuratedSymbolsDirIn_PicksTheHighestPatchOfTheSelectedMinor_AndNullWhenAbsent()
    {
        var root = TestScratch.Dir("al-runner-2768-symbols");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "28.1.100.0"));
            Directory.CreateDirectory(Path.Combine(root, "28.1.200.0"));
            Directory.CreateDirectory(Path.Combine(root, "27.5.999.0"));

            Assert.Equal(Norm(Path.Combine(root, "28.1.200.0")), Norm(BcArtifacts.CuratedSymbolsDirIn(root, "28.1")!));
            Assert.Null(BcArtifacts.CuratedSymbolsDirIn(root, "26.0"));
            Assert.Null(BcArtifacts.CuratedSymbolsDirIn(Path.Combine(root, "missing"), "28.1"));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // -------------------------------------------------------------- cache root resolver

    [Fact]
    public void CacheRoot_NoOverride_FallsBackToTheHomeRootedDefault()
    {
        Assert.Equal("/home/someone/.cache/al-runner",
            Norm(CacheRoots.ResolveDefaultRoot(null, () => "/home/someone")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CacheRoot_BlankOverride_FallsBackToTheHomeRootedDefault(string blank)
    {
        Assert.Equal("/home/someone/.cache/al-runner",
            Norm(CacheRoots.ResolveDefaultRoot(blank, () => "/home/someone")));
    }

    [Fact]
    public void CacheRoot_AbsoluteOverride_IsUsedVerbatim_WithoutConsultingHome()
    {
        var custom = AbsoluteFake("cache");
        Assert.Equal(Norm(custom), Norm(CacheRoots.ResolveDefaultRoot(custom + Path.DirectorySeparatorChar, HomeMustNotBeConsulted)));
    }

    [Fact]
    public void CacheRoot_RelativeOverride_IsAbsolutized()
    {
        var resolved = CacheRoots.ResolveDefaultRoot("rel-cache", () => "/home/someone");
        Assert.True(Path.IsPathRooted(resolved), $"expected a rooted path, got '{resolved}'");
        Assert.Equal(Norm(Path.GetFullPath("rel-cache")), Norm(resolved));
    }

    // ------------------------------------------------------------------ TestArtifacts gate

    [SkippableFact]
    public void TestArtifactsGate_FollowsTheArtifactsRootVariable_NotOnlyHome()
    {
        var emptyHome = TestScratch.Dir("al-runner-2768-gate-home");
        var relocated = TestScratch.Dir("al-runner-2768-gate-root");
        try
        {
            Directory.CreateDirectory(emptyHome);
            Directory.CreateDirectory(Path.Combine(relocated, "28.1.49838.53910"));

            // The home has nothing; the relocated root has a version directory.
            Assert.False(TestArtifacts.PresentIn(emptyHome, artifactsRootEnv: null));
            Assert.True(TestArtifacts.PresentIn(emptyHome, artifactsRootEnv: relocated));

            // On CI this is what used to fail the leg, naming a directory nobody populated.
            TestArtifacts.SkipIfMissingIn(emptyHome, relocated, runningOnCi: true);

            // And the reason names the root actually probed.
            Assert.Contains(Norm(Path.Combine(emptyHome, "nope")),
                Norm(TestArtifacts.MissingReason(emptyHome, Path.Combine(emptyHome, "nope"))));
        }
        finally
        {
            try { Directory.Delete(emptyHome, recursive: true); } catch { }
            try { Directory.Delete(relocated, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------ the real CLI, as a subprocess

    private static (int ExitCode, string Output) RunRunner(
        IReadOnlyDictionary<string, string?> env, string runnerArgs, string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")) + " " + runnerArgs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? RepoRoot,
        };
        // On the CHILD only: this process's environment is shared by parallel test classes.
        foreach (var name in new[] { BcArtifacts.ArtifactsRootEnvVar, BcArtifacts.SymbolsRootEnvVar,
                                     CacheRoots.CacheRootEnvVar, CacheRoots.NoCacheRootEnvVar })
            psi.Environment.Remove(name);
        foreach (var (k, v) in env)
        {
            if (v == null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }

        var sb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(SpawnTimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"al-runner did not exit within {SpawnTimeoutMs / 1000}s.");
        }
        proc.WaitForExit();
        lock (sb) return (proc.ExitCode, sb.ToString());
    }

    /// <summary>The provisioned artifacts root of THIS machine, honouring the variable.</summary>
    private static string ProvisionedArtifactsRootOrSkip()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestArtifacts.ArtifactsRootIn(TestArtifacts.HomeDir()!,
            Environment.GetEnvironmentVariable(BcArtifacts.ArtifactsRootEnvVar));
        TestArtifacts.SkipIf(!Directory.Exists(root) || !Directory.EnumerateDirectories(root).Any(),
            $"'{root}' is the runner-owned artifacts root this test hands to an isolated home, and it is not provisioned.");
        return root;
    }

    private static readonly string Fixture =
        "\"" + Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec") + "\"";

    [SkippableFact]
    public void Cli_WithTheRootsSet_WritesEveryCacheUnderThem_ScansTheSymbolsRoot_AndNothingUnderHome_WarmToo()
    {
        var artifactsRoot = ProvisionedArtifactsRootOrSkip();
        var isolatedHome = TestScratch.Dir("al-runner-2768-home");
        var cacheRoot = TestScratch.Dir("al-runner-2768-cache");
        var symbolsRoot = TestScratch.Dir("al-runner-2768-syms");
        Directory.CreateDirectory(isolatedHome);
        // One (empty) symbols version dir per provisioned version, so whichever BC version the
        // runner selects has a curated dir for its major.minor under the relocated root.
        foreach (var v in Directory.EnumerateDirectories(artifactsRoot))
            Directory.CreateDirectory(Path.Combine(symbolsRoot, Path.GetFileName(v)));
        var homeCache = Path.Combine(isolatedHome, ".cache", "al-runner");
        var env = new Dictionary<string, string?>
        {
            ["HOME"] = isolatedHome,
            ["USERPROFILE"] = isolatedHome,
            [BcArtifacts.ArtifactsRootEnvVar] = artifactsRoot,
            [CacheRoots.CacheRootEnvVar] = cacheRoot,
            [BcArtifacts.SymbolsRootEnvVar] = symbolsRoot,
        };
        try
        {
            // Cold: every cache is written, and all of it lands under the variable's root.
            var (exit1, out1) = RunRunner(env, "--verbose --no-auto-provision " + Fixture);
            Assert.True(exit1 == 0, $"cold run must succeed. exit={exit1}\n{out1}");
            Assert.Contains("1P/0F/0E", out1, StringComparison.Ordinal);
            Assert.Contains("[cache] WROTE", out1, StringComparison.Ordinal);
            Assert.Contains($"path={Norm(Path.Combine(cacheRoot, "al-out"))}", Norm(out1));
            Assert.True(Directory.EnumerateFiles(Path.Combine(cacheRoot, "al-out")).Any(), "al-out under the cache root is empty");
            Assert.True(Directory.Exists(Path.Combine(cacheRoot, "ncl-cecil")), "ncl-cecil was not written under the cache root");
            Assert.False(Directory.Exists(homeCache), $"the cold run wrote under '{homeCache}'");
            // The default package-cache scan reached the relocated symbols tree.
            Assert.Contains($"[pkg-cache] {Norm(symbolsRoot)}/", Norm(out1));

            // Warm, same root: the AL-output cache HITs from the relocated root, and home stays empty.
            var (exit2, out2) = RunRunner(env, "--verbose --no-auto-provision " + Fixture);
            Assert.True(exit2 == 0, $"warm run must succeed. exit={exit2}\n{out2}");
            Assert.Contains("1P/0F/0E", out2, StringComparison.Ordinal);
            Assert.Contains($"[cache] HIT  key=", out2, StringComparison.Ordinal);
            Assert.Contains($"path={Norm(Path.Combine(cacheRoot, "al-out"))}", Norm(out2));
            Assert.False(Directory.Exists(homeCache), $"the warm run wrote under '{homeCache}'");
        }
        finally
        {
            try { Directory.Delete(isolatedHome, recursive: true); } catch { }
            try { Directory.Delete(cacheRoot, recursive: true); } catch { }
            try { Directory.Delete(symbolsRoot, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void Cli_WithAnUnusableRelativeCacheRoot_ExitsTwo_NamingTheVariableAndTheAbsolutePath()
    {
        var artifactsRoot = ProvisionedArtifactsRootOrSkip();
        var cwd = TestScratch.Dir("al-runner-2768-cwd");
        Directory.CreateDirectory(cwd);
        // A regular FILE where the root directory would go: nothing can be created under it.
        File.WriteAllText(Path.Combine(cwd, "blocked"), "not a directory");
        try
        {
            var (exit, output) = RunRunner(new Dictionary<string, string?>
            {
                [BcArtifacts.ArtifactsRootEnvVar] = artifactsRoot,
                [CacheRoots.CacheRootEnvVar] = "blocked",
            }, "--verbose --no-auto-provision " + Fixture, workingDir: cwd);

            Assert.True(exit == 2, $"expected exit 2. exit={exit}\n{output}");
            Assert.Contains($"{CacheRoots.CacheRootEnvVar} '{Norm(Path.Combine(cwd, "blocked"))}", Norm(output));
            Assert.DoesNotContain("Unhandled exception", output, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(cwd, recursive: true); } catch { } }
    }
}

/// <summary>
/// One resolver per root: no production file other than the resolver's own spells the
/// home-rooted symbols or cache path. A second spelling is a read site the variable does not
/// reach — the silent split #2768 exists to prevent.
/// </summary>
public sealed class HomeRootedPathReadSiteGuardTests
{
    private static readonly string RunnerDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AlRunner"));

    private static readonly (string Root, Regex Spelling, string Owner)[] Roots =
    [
        ("symbols", new Regex("\"al-runner\"\\s*,\\s*\"symbols\"|\"[^\"~]*\\.local/share/al-runner/symbols"),
         Path.Combine("Infrastructure", "BcArtifacts.cs")),
        ("cache", new Regex("\"\\.cache\"\\s*,\\s*\"al-runner\"|\"[^\"~]*\\.cache/al-runner"),
         Path.Combine("Infrastructure", "CacheRoots.cs")),
    ];

    [Fact]
    public void OnlyTheResolverSpellsEachHomeRootedRoot()
    {
        var files = Directory.EnumerateFiles(RunnerDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(files.Count > 100, $"scanned only {files.Count} files under '{RunnerDir}' — wrong directory?");

        var offenders = new List<string>();
        var ownerHits = new Dictionary<string, int>();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(RunnerDir, file);
            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("*", StringComparison.Ordinal)) continue;
                foreach (var (root, spelling, owner) in Roots)
                {
                    if (!spelling.IsMatch(code)) continue;
                    if (rel == owner) ownerHits[root] = ownerHits.GetValueOrDefault(root) + 1;
                    else offenders.Add($"{root}: {rel}:{lineNo}: {code}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "home-rooted path spelled outside its resolver (route it through BcArtifacts.SymbolsRootDir / "
            + "CacheRoots.DefaultRoot):\n" + string.Join("\n", offenders));
        // The pattern must still match the resolver's own spelling, or the guard checks nothing.
        foreach (var (root, _, owner) in Roots)
            Assert.True(ownerHits.GetValueOrDefault(root) >= 1, $"the {root} pattern no longer matches {owner} itself");
    }
}
