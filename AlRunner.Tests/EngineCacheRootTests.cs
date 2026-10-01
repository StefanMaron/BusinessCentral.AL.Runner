// EngineCacheRootTests — #5109. AL_RUNNER_ENGINE_CACHE_ROOT keeps the engine caches (ncl-cecil,
// ncl-shadow) under one directory whatever --cache / --no-cache says, so many runs with private
// AL caches share one engine build. CacheRootsEngineRootResolveTests pins the path arithmetic
// in-process; this class proves it through the real runner, in both directions, and the refusal.
//
// Runner infrastructure only — AL cannot observe a cache root — so nothing here belongs in the
// al-language corpus.

using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class EngineCacheRootTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private static string WriteBundle(string scratch)
    {
        var bundle = Path.Combine(scratch, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "app.json"), """
        { "id": "b5109000-0000-4000-8000-000000005109", "name": "EngineRoot5109", "publisher": "AL Runner",
          "version": "1.0.0.0", "idRanges": [ { "from": 64910, "to": 64919 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(bundle, "Probe.al"), """
        codeunit 64910 "EngineRoot5109 Tests"
        {
            Subtype = Test;
            [Test]
            procedure Sums()
            begin
                if 2 + 3 <> 5 then Error('sum');
            end;
        }
        """);
        return bundle;
    }

    private static (string Output, int Exit) Run(string bundle, string? engineRoot, params string[] cacheArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" --package-cache \"{platformApps}\"");
        foreach (var a in cacheArgs) args.Append(' ').Append(a);
        args.Append($" --verbose --no-auto-provision \"{bundle}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        // This class sets the variable per run, to a root it owns, so its cold/warm state is known.
        SharedEngineCaches.Isolate(psi);
        if (engineRoot != null) psi.Environment[CacheRoots.EngineCacheRootEnvVar] = engineRoot;
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

    private static string[] PublishedShadowDirs(string root)
    {
        var shadowRoot = Path.Combine(root, "ncl-shadow");
        return Directory.Exists(shadowRoot)
            ? Directory.GetDirectories(shadowRoot).Where(d => !Path.GetFileName(d).Contains(".building.")).ToArray()
            : Array.Empty<string>();
    }

    [SkippableFact]
    public void EngineRoot_HoldsTheEngineCaches_ForCacheAndNoCacheRuns_AndIsReusedWarm()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-5109-engine-root");
        var bundle = WriteBundle(scratch);
        var engineRoot = Path.Combine(scratch, "engine");
        var cacheA = Path.Combine(scratch, "cache-a");
        var cacheB = Path.Combine(scratch, "cache-b");

        // Cold engine root: the build happens there, and nothing engine-side under --cache.
        var (cold, coldExit) = Run(bundle, engineRoot, "--cache", $"\"{cacheA}\"");
        Assert.True(coldExit == 0 && cold.Contains("passed 1 "), $"cold run must pass:\n{cold}");
        Assert.Contains("[Cecil] Cecil cache MISS", cold);
        var engineCecil = Path.Combine(engineRoot, "ncl-cecil");
        Assert.True(Directory.Exists(engineCecil) && Directory.GetFiles(engineCecil, "*.dll").Length > 0,
            $"the engine root holds no ncl-cecil entry after a run that was told to use it:\n{cold}");
        Assert.Single(PublishedShadowDirs(engineRoot));
        foreach (var name in CacheRoots.EngineCacheNames)
            Assert.False(Directory.Exists(Path.Combine(cacheA, name)), $"{name} was built under --cache '{cacheA}':\n{cold}");
        // The other caches still follow --cache.
        Assert.True(Directory.Exists(Path.Combine(cacheA, "bc-symbols")), $"bc-symbols left --cache '{cacheA}':\n{cold}");

        // A different, fresh --cache root reuses the engine build instead of repeating it.
        var (warm, warmExit) = Run(bundle, engineRoot, "--cache", $"\"{cacheB}\"");
        Assert.True(warmExit == 0 && warm.Contains("passed 1 "), $"warm run must pass:\n{warm}");
        Assert.Contains("[Cecil] Cecil cache HIT", warm);
        Assert.Contains("[Cecil] Reusing Ncl shadow runtime dir", warm);
        Assert.DoesNotContain("[Cecil] Cecil cache MISS", warm);
        foreach (var name in CacheRoots.EngineCacheNames)
            Assert.False(Directory.Exists(Path.Combine(cacheB, name)), $"{name} was built under --cache '{cacheB}':\n{warm}");

        // --no-cache moves every other cache to a throwaway root; the engine caches stay put.
        var (noCache, noCacheExit) = Run(bundle, engineRoot, "--no-cache");
        Assert.True(noCacheExit == 0 && noCache.Contains("passed 1 "), $"--no-cache run must pass:\n{noCache}");
        Assert.Contains("[Cecil] Cecil cache HIT", noCache);
        Assert.Contains("[Cecil] Reusing Ncl shadow runtime dir", noCache);
        Assert.Single(PublishedShadowDirs(engineRoot));
    }

    [SkippableFact]
    public void UnusableEngineRoot_ExitsTwo_NamingTheVariableAndTheValue()
    {
        TestArtifacts.SkipIfMissing();
        var scratch = TestScratch.Dir("al-runner-5109-engine-root-unusable");
        var bundle = WriteBundle(scratch);
        var file = Path.Combine(scratch, "a-file");
        File.WriteAllText(file, "not a directory");
        var underAFile = Path.Combine(file, "engine");

        var (output, exit) = Run(bundle, underAFile, "--cache", $"\"{Path.Combine(scratch, "cache")}\"");

        Assert.Equal(2, exit);
        Assert.Contains($"{CacheRoots.EngineCacheRootEnvVar} '{underAFile}' is not a usable directory path", output);
        Assert.DoesNotContain("passed ", output);
    }
}

[Collection(CacheRootsSerialCollection.Name)]
public sealed class CacheRootsEngineRootResolveTests
{
    [Fact]
    public void Resolve_WithAnEngineRoot_MovesOnlyTheEngineCaches_UnderEveryOtherRootChoice()
    {
        var engine = TestScratch.Dir("al-runner-5109-engine-unit");
        var overrideDir = TestScratch.Dir("al-runner-5109-override-unit");
        CacheRoots.ResetForTests();
        try
        {
            CacheRoots.SetOverride(overrideDir);
            CacheRoots.SetEngineRoot(engine);
            Assert.True(Directory.Exists(engine), "SetEngineRoot did not create the directory it was given");
            Assert.Equal(Path.Combine(engine, "ncl-cecil"), CacheRoots.Resolve("ncl-cecil"));
            Assert.Equal(Path.Combine(engine, "ncl-shadow"), CacheRoots.Resolve("ncl-shadow"));
            foreach (var other in new[] { "compiled-deps", "workspace-deps", "bc-symbols", "r2r-chunks", "app-manifests" })
                Assert.Equal(Path.Combine(overrideDir, other), CacheRoots.Resolve(other));

            // No --cache at all: still only the engine caches move.
            CacheRoots.SetOverride(null);
            Assert.Equal(Path.Combine(engine, "ncl-cecil"), CacheRoots.Resolve("ncl-cecil"));
            Assert.Equal(Path.Combine(CacheRoots.DefaultRoot, "compiled-deps"), CacheRoots.Resolve("compiled-deps"));
        }
        finally { CacheRoots.ResetForTests(); }
    }

    [Fact]
    public void SetEngineRoot_BlankOrNull_LeavesResolveOnTheOverride()
    {
        var overrideDir = TestScratch.Dir("al-runner-5109-override-unit");
        CacheRoots.ResetForTests();
        try
        {
            CacheRoots.SetOverride(overrideDir);
            foreach (var blank in new[] { null, "", "   " })
            {
                CacheRoots.SetEngineRoot(TestScratch.Dir("al-runner-5109-engine-unit"));
                CacheRoots.SetEngineRoot(blank);
                Assert.Equal(Path.Combine(overrideDir, "ncl-cecil"), CacheRoots.Resolve("ncl-cecil"));
                Assert.Equal(Path.Combine(overrideDir, "ncl-shadow"), CacheRoots.Resolve("ncl-shadow"));
            }
        }
        finally { CacheRoots.ResetForTests(); }
    }

    [Fact]
    public void SetEngineRoot_ARelativeValue_IsRootedOnce()
    {
        CacheRoots.ResetForTests();
        var relative = Path.Combine("al-runner-5109-rel", Guid.NewGuid().ToString("N"));
        try
        {
            CacheRoots.SetEngineRoot(relative);
            Assert.Equal(Path.Combine(Path.GetFullPath(relative), "ncl-cecil"), CacheRoots.Resolve("ncl-cecil"));
        }
        finally
        {
            CacheRoots.ResetForTests();
            try { Directory.Delete(Path.GetFullPath("al-runner-5109-rel"), recursive: true); } catch { }
        }
    }
}
