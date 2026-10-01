// SharedEngineCachesGuardTests — #5109. Holds SharedEngineCaches in place: every runner the test
// host spawns shares one engine cache root, a fresh --cache root built through any helper gets no
// private engine build, and no spawn site sets or removes the variable except through
// SharedEngineCaches.Isolate (or EngineCacheRootTests, whose subject is the variable itself).
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class SharedEngineCachesGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string TestsDir = Path.Combine(RepoRoot, "AlRunner.Tests");

    // The only files allowed to name the variable or write it.
    private static readonly string[] Owners =
        { "SharedEngineCaches.cs", "SharedEngineCachesGuardTests.cs", "EngineCacheRootTests.cs" };

    [Fact]
    public void TestHost_PinsOneEngineCacheRoot_ForEveryRunnerItSpawns()
    {
        Assert.Equal(AlRunner.Infrastructure.CacheRoots.EngineCacheRootEnvVar, SharedEngineCaches.EnvVar);
        Assert.True(Path.IsPathRooted(SharedEngineCaches.Root), $"shared engine root is not absolute: {SharedEngineCaches.Root}");
        Assert.Equal(SharedEngineCaches.Root, Environment.GetEnvironmentVariable(SharedEngineCaches.EnvVar));
        // What a spawn site that sets nothing hands its child.
        Assert.Equal(SharedEngineCaches.Root, new ProcessStartInfo("dotnet").Environment[SharedEngineCaches.EnvVar]);

        var isolated = new ProcessStartInfo("dotnet");
        SharedEngineCaches.Isolate(isolated);
        Assert.False(isolated.Environment.ContainsKey(SharedEngineCaches.EnvVar));
    }

    // The end-to-end form of the pin: a spawn site written the ordinary way — a fresh --cache root,
    // nothing set on the environment — builds no engine cache under that root.
    [SkippableFact]
    public void OrdinarySpawn_WithAFreshCacheRoot_BuildsNoEngineCacheUnderIt()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-5109-pin-spawn");
        var bundle = Path.Combine(scratch, "bundle");
        var cache = Path.Combine(scratch, "cache");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "Probe.al"), """
        codeunit 64911 "SharedEngine5109 Tests"
        {
            Subtype = Test;
            [Test]
            procedure Sums()
            begin
                if 1 + 4 <> 5 then Error('sum');
            end;
        }
        """);
        var args = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
        args.Append($" --cache \"{cache}\" \"{bundle}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        var output = stdout + stderr.Result;

        Assert.True(p.ExitCode == 0 && output.Contains("passed 1 "), $"run must pass:\n{output}");
        // The run did write under --cache, so an absent engine dir is not an absent run.
        Assert.True(Directory.Exists(Path.Combine(cache, "bc-symbols")), $"nothing was written under --cache '{cache}':\n{output}");
        foreach (var name in new[] { "ncl-cecil", "ncl-shadow" })
            Assert.False(Directory.Exists(Path.Combine(cache, name)),
                $"{name} was built under the fresh --cache root '{cache}' instead of the shared engine root "
                + $"'{SharedEngineCaches.Root}' — the test host's pin did not reach this spawn:\n{output}");
        Assert.True(Directory.Exists(Path.Combine(SharedEngineCaches.Root, "ncl-cecil")),
            $"the shared engine root '{SharedEngineCaches.Root}' holds no ncl-cecil after a run that used it");
    }

    private static readonly Regex[] GoesAround =
    {
        new(Regex.Escape(SharedEngineCaches.EnvVar), RegexOptions.CultureInvariant),
        new(@"SharedEngineCaches\s*\.\s*EnvVar", RegexOptions.CultureInvariant),
        new(@"\bEngineCacheRootEnvVar\b", RegexOptions.CultureInvariant),
        new(@"\bSetEngineRoot\s*\(", RegexOptions.CultureInvariant),
    };

    internal static bool GoesAroundThePin(string line) => GoesAround.Any(r => r.IsMatch(line));

    [Theory]
    [InlineData("psi.Environment[\"AL_RUNNER_ENGINE_CACHE_ROOT\"] = dir;")]
    [InlineData("psi.Environment.Remove(SharedEngineCaches.EnvVar);")]
    [InlineData("psi.Environment[CacheRoots.EngineCacheRootEnvVar] = dir;")]
    [InlineData("psi.Environment.Remove(AlRunner.Infrastructure.CacheRoots.EngineCacheRootEnvVar);")]
    [InlineData("CacheRoots.SetEngineRoot(dir);")]
    public void Scan_CatchesEverySpellingOfThePin(string line)
        => Assert.True(GoesAroundThePin(line), $"the scan misses: {line}");

    [Theory]
    [InlineData("SharedEngineCaches.Isolate(psi);")]
    [InlineData("var root = SharedEngineCaches.Root;")]
    [InlineData("psi.Environment[CacheRoots.CacheRootEnvVar] = dir;")]
    [InlineData("psi.Environment[\"AL_RUNNER_CACHE_ROOT\"] = dir;")]
    public void Scan_LeavesSanctionedAndUnrelatedLinesAlone(string line)
        => Assert.False(GoesAroundThePin(line), $"the scan flags a line that does not go around the pin: {line}");

    [Fact]
    public void NoSpawnSite_SetsOrRemovesThePin_OutsideItsOwners()
    {
        var files = Directory.Exists(TestsDir)
            ? Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToList()
            : new List<string>();
        // Could not measure: a scan over nothing would pass whatever the spawn sites do.
        Assert.True(files.Count > 100 && files.Any(f => Path.GetFileName(f) == Owners[0]),
            $"scanned {files.Count} file(s) under '{TestsDir}', which is not the test project's source; the guard measured nothing.");

        var hits = new StringBuilder();
        foreach (var f in files.Where(f => !Owners.Contains(Path.GetFileName(f))))
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
                if (GoesAroundThePin(lines[i]))
                    hits.AppendLine($"{Path.GetRelativePath(RepoRoot, f)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(hits.Length == 0,
            "a spawn site goes around the shared engine cache root (#5109). Use SharedEngineCaches.Isolate "
            + "for a test whose subject is a cold or isolated engine cache:\n" + hits);
    }
}
