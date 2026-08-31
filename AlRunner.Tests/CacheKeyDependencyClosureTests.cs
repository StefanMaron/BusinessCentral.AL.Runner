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
        => RunRunner(FixturePath, packageCacheDir, alCacheDir, extraArgs);

    private static string RunRunner(string bundlePath, string packageCacheDir, string alCacheDir, string extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundlePath}\"");
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
        => RunAndReadCacheKeyOnly(FixturePath, packageCacheDir, alCacheDir);

    private static string RunAndReadCacheKeyOnly(string bundlePath, string packageCacheDir, string alCacheDir)
    {
        var output = RunRunner(bundlePath, packageCacheDir, alCacheDir, " --print-cache-key");
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
        // Issue #2239: "[cache] MISS/HIT key=..." moved behind --verbose (diagnostic
        // detail, not a test result) — this is the one caller that still needs to read
        // it back off real output, so it passes the flag explicitly.
        var output = RunRunner(packageCacheDir, alCacheDir, " --verbose");
        var m = Regex.Match(output, @"\[cache\]\s+(?:MISS|HIT)\s+key=([0-9a-f]{64})");
        Assert.True(m.Success, $"could not read a cache key from the runner output:\n{output}");
        return m.Groups[1].Value;
    }

    /// <summary>
    /// Two different dependency closures over byte-identical AL sources must produce two
    /// different keys. Reverting GetOrderedDepIds to resolve without the bundle's
    /// .alpackages makes both keys collapse to the same value and fails this.
    ///
    /// The closure is varied with a SYNTHETIC third-party dependency at two different
    /// versions, and the bundle declares no `application`/`platform` at all, so nothing the
    /// runner can provision is part of the answer.
    ///
    /// It used to vary the closure by copying the real Microsoft platform apps and dropping
    /// System.app from one side. That has now failed twice for reasons unrelated to the
    /// cache key, and both times the fix was to pick a different app to drop:
    /// <list type="number">
    /// <item>provisioning started fetching Application Test Library, so "the ordinally-first
    /// .app" became one this fixture never resolves;</item>
    /// <item>issue #2205 made the platform set a real, detected need, so the runner-owned
    /// `&lt;artifacts&gt;/&lt;version&gt;/platform-apps` directory folds into the search set
    /// (and, on a cold machine, gets downloaded) — which supplies System.app right back to
    /// the "reduced" side.</item>
    /// </list>
    /// Both runs then keyed on the same closure and the test failed while the cache key was
    /// working correctly. Worse, it PASSED on a developer machine for an accidental reason:
    /// the key stamps each winning `.app`'s mtime+size, and the freshly-copied file and the
    /// warm provisioned one merely had different timestamps. A test whose verdict depends on
    /// the machine's provisioning history is not measuring the cache key. This variant
    /// cannot be restored by any provisioning path, because no artifact Microsoft ships is
    /// published by "Contoso ISV".
    /// </summary>
    [SkippableFact]
    public void DifferentDependencyClosure_ProducesDifferentCacheKey()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteIsvDependentFixture(Path.Combine(_scratch, "isv-bundle"));
        var v1 = Path.Combine(_scratch, "isv-v1");
        var v2 = Path.Combine(_scratch, "isv-v2");
        Directory.CreateDirectory(v1);
        Directory.CreateDirectory(v2);
        WriteSyntheticApp(v1, IsvDepId, "Contoso Dep", "Contoso ISV", "1.0.0.0");
        WriteSyntheticApp(v2, IsvDepId, "Contoso Dep", "Contoso ISV", "1.4.0.0");

        var keyV1 = RunAndReadCacheKeyOnly(bundle, v1, Path.Combine(_scratch, "cache-isv-v1"));
        var keyV2 = RunAndReadCacheKeyOnly(bundle, v2, Path.Combine(_scratch, "cache-isv-v2"));

        Assert.NotEqual(keyV1, keyV2);
    }

    /// <summary>
    /// The companion to the above at the same isolation: the SAME synthetic closure must key
    /// identically across runs. Without this, "the key changed" above would be satisfied by a
    /// key that simply varies, which would destroy the cache rather than track the closure.
    /// </summary>
    [SkippableFact]
    public void SameIsvDependencyClosure_ProducesStableCacheKey()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteIsvDependentFixture(Path.Combine(_scratch, "isv-bundle-stable"));
        var dir = Path.Combine(_scratch, "isv-stable");
        Directory.CreateDirectory(dir);
        WriteSyntheticApp(dir, IsvDepId, "Contoso Dep", "Contoso ISV", "1.0.0.0");

        var alCache = Path.Combine(_scratch, "cache-isv-stable");
        var first = RunAndReadCacheKeyOnly(bundle, dir, alCache);
        var second = RunAndReadCacheKeyOnly(bundle, dir, alCache);

        Assert.Equal(first, second);
    }

    private const string IsvDepId = "b7e1c0de-1111-4222-8333-444455556666";

    /// <summary>A one-codeunit bundle declaring exactly one third-party dependency and NO
    /// `application`/`platform` — so its resolved closure is entirely under this test's
    /// control and no provisioning decision touches it.</summary>
    private static string WriteIsvDependentFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "3f9a1c22-5b7d-4e10-9c33-8a0e1d2b4c56",
          "name": "CacheKey ISV Dep Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{IsvDepId}}", "name": "Contoso Dep", "publisher": "Contoso ISV", "version": "1.0.0.0" }
          ],
          "idRanges": [ { "from": 60700, "to": 60709 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "CacheKeyIsv.Codeunit.al"), """
        codeunit 60700 "CacheKey Isv Probe"
        {
            procedure Probe(): Integer
            begin
                exit(1);
            end;
        }
        """);
        return dir;
    }

    /// <summary>Writes a minimal NAVX `.app` — enough for AppLoader.ReadManifest and
    /// DependencyResolver to see the package and its version.</summary>
    private static void WriteSyntheticApp(
        string dir, string appId, string name, string publisher, string version)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
              <Dependencies />
            </Package>
            """;
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using var es = entry.Open();
            es.Write(Encoding.UTF8.GetBytes(xml));
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        File.WriteAllBytes(Path.Combine(dir, $"{publisher}_{name}_{version}.app"), result);
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
