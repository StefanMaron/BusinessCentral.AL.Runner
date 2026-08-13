// CacheKeyDependencyClosureTests — the compile cache key must follow the resolved
// dependency closure, not just the AL sources.
//
// The emitted DLL depends on which packages won resolution, so two runs over identical
// sources against different closures are different compilations. If the key ignores the
// closure, the second run gets a HIT and executes a DLL compiled against the first run's
// dependencies.
//
// This was real, and the omission was total rather than partial: GetOrderedDepIds resolved
// against the package caches ALONE, without the bundle's own .alpackages. A bundle whose
// roots live in its .alpackages therefore could not resolve at all, the exception hit a
// bare `catch { return Array.Empty<string>(); }`, and the key carried NO dep line. Measured
// on the al-language corpus: adding a System.app package changed the emitted DLL
// (3175424 -> 3206144 bytes) while the key stayed identical at
// 67c4f8c4622a928aae07bf1857af515bb37fc5df4ac16eb047855f5dd2f9bba8.
//
// Same defect family as --define preprocessor symbols missing from this key.
//
// ── issue #1851: this used to cost 286.7s across four cold AL compiles ─────────────
// Both tests below assert a property of the cache KEY string alone — never anything about
// a compiled DLL, an emitted assembly, or executed tests — yet the key is computed BEFORE
// Emit+Compile even runs (see Program.cs's ComputeAlCacheKey call site). Spawning the
// runner to completion for a key comparison was paying for a full cold AL compile per
// invocation to answer a question the compile never touches.
//
// `--print-cache-key` (added alongside this test change) reaches that SAME
// ComputeAlCacheKey call, with the SAME arguments, then prints and exits before
// Emit+Compile starts — no second/parallel key computation, so these two tests still prove
// exactly what they proved before, just without paying for the compile. The one thing that
// change could break silently — key-only mode drifting from what a real run actually keys
// on — is what PrintCacheKeyOnly_MatchesFullRunKey below exists to catch: it is the one
// test in this class still allowed to pay for a full compile, because it is the anchor
// holding the cheap path to reality.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

// See DefineFlagIntegrationTests for why runner-subprocess tests used to be
// [Collection("server-serial")] and no longer are — #1809.
public sealed class CacheKeyDependencyClosureTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");
    private static readonly string FixturePath = Path.Combine(
        RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

    private readonly string _scratch;

    public CacheKeyDependencyClosureTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "al-runner-cachekey", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>Spawns the runner with the given extra args against the fixture and returns its combined output.</summary>
    private static string RunRunner(string packageCacheDir, string alCacheDir, string extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{FixturePath}\"");
        args.Append($" --package-cache \"{packageCacheDir}\"");
        args.Append($" --cache \"{alCacheDir}\"");
        args.Append(extraArgs);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return sb.ToString();
    }

    /// <summary>
    /// Fast path: runs the fixture in `--print-cache-key` mode, which reaches the exact same
    /// ComputeAlCacheKey call a real run would (see Program.cs) and exits before Emit+Compile.
    /// This is what the two behavioural tests below use — they only ever assert a property of
    /// the key string, so there is nothing lost by not paying for the compile.
    /// </summary>
    private static string RunAndReadCacheKeyOnly(string packageCacheDir, string alCacheDir)
    {
        var output = RunRunner(packageCacheDir, alCacheDir, " --print-cache-key");
        var m = Regex.Match(output, @"\[cache\]\s+KEY\s+key=([0-9a-f]{64})");
        Assert.True(m.Success, $"could not read a cache key from --print-cache-key output:\n{output}");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// Slow path: runs the fixture to a REAL cold compile (no --print-cache-key) and reads the
    /// key off the [cache] MISS/HIT line. Only PrintCacheKeyOnly_MatchesFullRunKey below still
    /// calls this — every other test uses the fast RunAndReadCacheKeyOnly path (issue #1851).
    /// </summary>
    private static string RunFullAndReadCacheKey(string packageCacheDir, string alCacheDir)
    {
        var output = RunRunner(packageCacheDir, alCacheDir, "");
        var m = Regex.Match(output, @"\[cache\]\s+(?:MISS|HIT)\s+key=([0-9a-f]{64})");
        Assert.True(m.Success, $"could not read a cache key from the runner output:\n{output}");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// Two different dependency closures over byte-identical AL sources must produce two
    /// different keys. Reverting GetOrderedDepIds to resolve without the bundle's
    /// .alpackages makes both keys collapse to the same value and fails this.
    /// </summary>
    [SkippableFact]
    public void DifferentDependencyClosure_ProducesDifferentCacheKey()
    {
        TestArtifacts.SkipIfMissing();
        var platformApps = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(platformApps, "R2R platform apps");

        var full = Path.Combine(_scratch, "full");
        var reduced = Path.Combine(_scratch, "reduced");
        Directory.CreateDirectory(full);
        Directory.CreateDirectory(reduced);

        var apps = Directory.GetFiles(platformApps, "*.app");
        TestArtifacts.SkipIf(apps.Length < 2,
            $"varying the dependency closure needs >= 2 platform apps; '{platformApps}' holds {apps.Length}.");

        foreach (var a in apps) File.Copy(a, Path.Combine(full, Path.GetFileName(a)));

        // Same closure minus exactly one package — and it has to be a package the bundle
        // actually RESOLVES, or the two closures come out identical and this asserts nothing.
        //
        // Dropping the ordinally-first .app used to satisfy that by accident: the directory
        // held only the platform apps, so "first" was Microsoft_Application_*. Once
        // provisioning also fetched Application Test Library, "first" became a package this
        // fixture never references — both keys matched and the test failed while the cache
        // key was working correctly. Pick the platform root explicitly instead: every AL app
        // resolves `System` (the manifest's `platform` root), so removing it is guaranteed to
        // be a different compile input regardless of what else provisioning drops in here.
        var platformRoot = apps.FirstOrDefault(
            a => Path.GetFileName(a).Equals("System.app", StringComparison.OrdinalIgnoreCase));
        Assert.True(platformRoot != null,
            $"platform-apps must contain System.app to vary the closure; found: " +
            string.Join(", ", apps.Select(Path.GetFileName)));
        foreach (var a in apps.Where(a => a != platformRoot))
            File.Copy(a, Path.Combine(reduced, Path.GetFileName(a)));

        var keyFull = RunAndReadCacheKeyOnly(full, Path.Combine(_scratch, "cache-full"));
        var keyReduced = RunAndReadCacheKeyOnly(reduced, Path.Combine(_scratch, "cache-reduced"));

        Assert.NotEqual(keyFull, keyReduced);
    }

    /// <summary>
    /// The other direction, and the one that makes the test above meaningful rather than
    /// merely "the key changes a lot": an UNCHANGED closure over unchanged sources must
    /// produce the SAME key, so the cache still hits. A key that varied on every run would
    /// satisfy the inequality above while destroying the cache.
    /// </summary>
    [SkippableFact]
    public void SameDependencyClosure_ProducesStableCacheKey()
    {
        TestArtifacts.SkipIfMissing();
        var platformApps = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(platformApps, "R2R platform apps");

        var alCache = Path.Combine(_scratch, "cache-stable");
        var first = RunAndReadCacheKeyOnly(platformApps, alCache);
        var second = RunAndReadCacheKeyOnly(platformApps, alCache);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Guard test (issue #1851): the ONE test in this class still allowed to pay for a full
    /// cold compile, because it is what anchors --print-cache-key's cheap path to reality. If
    /// the key-only mode ever computed its key a different way than a real run — a second,
    /// parallel ComputeAlCacheKey call instead of reaching the real one and short-circuiting —
    /// this is the test that would catch it. Without it, the two tests above would only prove
    /// that --print-cache-key is self-consistent with itself, which is worthless.
    /// </summary>
    [SkippableFact]
    public void PrintCacheKeyOnly_MatchesFullRunKey()
    {
        TestArtifacts.SkipIfMissing();
        var platformApps = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(platformApps, "R2R platform apps");

        // Different --cache dirs on purpose: the cache key is a pure function of the AL
        // sources, resolved dep closure, module name, defines and runner fingerprint — NOT of
        // the cache directory path — so this also proves the key doesn't accidentally fold the
        // scratch path into itself.
        var fullRunKey = RunFullAndReadCacheKey(platformApps, Path.Combine(_scratch, "cache-full-run"));
        var keyOnlyKey = RunAndReadCacheKeyOnly(platformApps, Path.Combine(_scratch, "cache-key-only"));

        Assert.Equal(fullRunKey, keyOnlyKey);
    }
}
