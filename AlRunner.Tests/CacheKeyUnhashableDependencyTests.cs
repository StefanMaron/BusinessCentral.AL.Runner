// CacheKeyUnhashableDependencyTests — the degraded path of #2754's content-hashed dep terms:
// what the AL-output cache key does when ONE resolved dependency package cannot be hashed.
//
// The defect this pins (found in review of the #2754 fix, not in the original code)
// -------------------------------------------------------------------------------
// The first cut of the fix threw out of the per-dependency Select when
// ComputeAppContentHash could not answer, and let GetOrderedDepIds' outer catch key the
// whole bundle on one string:
//
//     return new[] { $"unresolved:{ex.GetType().Name}:{ex.Message}" };
//
// That is strictly WORSE than the `mtime:length` stamp it replaced. The old code degraded
// the single bad term to "?" and kept every other dependency's stamp; collapsing the LIST
// discards all of them. So:
//
//     run 1: dep X unhashable, dep Y at content C1  ->  key = f("unresolved:<type>:<msg>")
//     run 2: dep X unhashable, dep Y at content C2  ->  SAME string, SAME key, HIT
//
// and the DLL compiled against C1 is served for a closure containing C2. An exception type
// and message are deterministic across runs, so this HITs reliably — inheriting the exact
// property #2754 exists to remove, and the exact property the #2754 PR body criticised in
// the old "?" term ("simply cannot HIT", which was never true).
//
// Constructing "resolved but unhashable"
// -------------------------------------
// It looks impossible from outside the process: DependencyResolver.EnsureIndexed calls
// AppLoader.ReadManifest on every .app it finds, so a package it cannot open is a package it
// never indexes, and the run fails at resolution instead of reaching the hash.
//
// But ReadManifest is backed by an on-disk index under CacheRoots.Resolve("app-manifests"),
// keyed by (full path, length, last-write-ticks) — and `chmod 000` changes none of those. So:
// run once with the package readable to warm that index (the same --cache root is shared
// across all runs here, which is what makes the index visible to the next process), then make
// it unreadable. The resolver now answers from the index WITHOUT opening the file, and
// ComputeAppContentHash — which must read the bytes — throws.
//
// That construction is also the review finding's other half in concrete form: "the resolver
// read this package's manifest moments ago" does not imply the bytes are readable, so
// "unhashable means the compile is about to fail anyway" is an assumption, not a fact.
//
// The three arms
// --------------
// NEGATIVE (the regression) — X unhashable, Y's content CHANGES between two runs. The keys
// must differ. This is the arm that fails against the throw-and-collapse code.
//
// CONTROL — X unhashable, nothing else changes. The keys must be EQUAL. Without it the
// negative arm would be satisfied by a degraded term that simply varies per run (a nonce),
// which would force a permanent MISS for any bundle with an unreadable dependency instead of
// tracking what actually changed.
//
// LOUD — the run must SAY it keyed a dependency on something weaker than its content, naming
// the package. A cache silently downgrading its own identity is the failure mode
// .claude/rules/loud-failures.md exists for.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class CacheKeyUnhashableDependencyTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string DepXId = "c4102754-aaaa-4b22-8c33-d44455566677";
    private const string DepYId = "c4102754-bbbb-4b22-8c33-d44455566677";
    private const string BundleAppId = "c4102754-cccc-4333-8444-555566667777";

    private readonly string _scratch;
    private readonly List<string> _lockedFiles = new();

    public CacheKeyUnhashableDependencyTests()
    {
        _scratch = TestScratch.Dir("al-runner-cachekey-unhashable");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        // Restore modes first or the recursive delete trips on the locked files.
        foreach (var f in _lockedFiles)
        {
            try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// Makes <paramref name="path"/> unreadable and returns true only if that actually bites.
    /// A CAPABILITY probe rather than a uid check, for the same reason
    /// InaccessibleDirectoryScanTests uses one: running as root, or on a filesystem that
    /// ignores mode bits, the mode change is accepted and then ignored, and a test that
    /// assumed otherwise would assert against a perfectly readable file.
    /// </summary>
    private bool TryMakeUnreadable(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.None); }
        catch { return false; }
        _lockedFiles.Add(path);
        try
        {
            using var _ = File.OpenRead(path);
            return false; // the mode bits did not bite
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
    }

    /// <summary>
    /// The shared setup all three arms use: a bundle depending on X and Y, both packages
    /// written and resolvable, one warm-up run to populate the app-manifest index under the
    /// shared cache root, then X made unreadable.
    /// </summary>
    private (string Bundle, string PkgDir, string CacheDir, string XPath, string YPath) ArrangeUnhashableX(string name)
    {
        var bundle = WriteTwoDepFixture(Path.Combine(_scratch, name + "-bundle"));
        var pkgDir = Path.Combine(_scratch, name + "-pkg");
        Directory.CreateDirectory(pkgDir);
        var cacheDir = Path.Combine(_scratch, name + "-cache");

        var xPath = WriteSyntheticApp(pkgDir, DepXId, "Fabrikam Dep X", payloadFill: (byte)'X');
        var yPath = WriteSyntheticApp(pkgDir, DepYId, "Fabrikam Dep Y", payloadFill: (byte)'1');

        // Warm-up run, with BOTH packages readable: this is what writes the app-manifests index
        // entries the later runs resolve X from without opening it.
        var warm = RunPrintCacheKey(bundle, pkgDir, cacheDir);
        Assert.True(warm.Key != null, $"warm-up run produced no cache key:\n{warm.Output}");

        Skip.IfNot(TryMakeUnreadable(xPath),
            "cannot make a file unreadable here (running as root, or a filesystem that ignores "
            + "mode bits), so 'resolved but unhashable' cannot be constructed");

        return (bundle, pkgDir, cacheDir, xPath, yPath);
    }

    /// <summary>
    /// THE REGRESSION ARM. One unhashable dependency must not erase the identity of the others:
    /// with X unhashable throughout, changing Y's bytes must still change the key.
    ///
    /// Against the throw-and-collapse code both runs key on the identical
    /// `unresolved:UnauthorizedAccessException:Access to the path '…' is denied.` string, the
    /// keys are equal, and a warm cache serves the DLL compiled against Y's previous bytes.
    /// </summary>
    [SkippableFact]
    public void UnhashableDependency_DoesNotHideAnotherDependencysContentChange()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir, _, yPath) = ArrangeUnhashableX("regression");

        var before = RunPrintCacheKey(bundle, pkgDir, cacheDir);
        Assert.True(before.Key != null, $"no cache key with X unreadable:\n{before.Output}");

        // Y keeps its declared id/name/publisher/version and changes only its bytes — the same
        // shape #2754 is about, now with an unhashable sibling standing next to it.
        var yBefore = File.ReadAllBytes(yPath);
        WriteSyntheticApp(pkgDir, DepYId, "Fabrikam Dep Y", payloadFill: (byte)'2');
        Assert.False(yBefore.AsSpan().SequenceEqual(File.ReadAllBytes(yPath)),
            "Y's bytes did not change — the fixture is not exercising the defect");

        var after = RunPrintCacheKey(bundle, pkgDir, cacheDir);
        Assert.True(after.Key != null, $"no cache key after rewriting Y:\n{after.Output}");

        Assert.NotEqual(before.Key, after.Key);
    }

    /// <summary>
    /// THE CONTROL. With X unhashable and nothing else touched, the key must be STABLE. This is
    /// what forbids "fixing" the arm above with a per-run nonce: a degraded term that simply
    /// varies would satisfy the inequality while forcing a permanent MISS on every bundle that
    /// has an unreadable dependency, which is a different way of throwing the cache away.
    /// </summary>
    [SkippableFact]
    public void UnhashableDependency_WithNothingElseChanged_KeysStably()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir, _, _) = ArrangeUnhashableX("control");

        var first = RunPrintCacheKey(bundle, pkgDir, cacheDir);
        var second = RunPrintCacheKey(bundle, pkgDir, cacheDir);

        Assert.True(first.Key != null, $"no cache key on the first run:\n{first.Output}");
        Assert.Equal(first.Key, second.Key);
    }

    /// <summary>
    /// THE LOUD ARM. Downgrading a dependency's cache identity from its content to a path+stat
    /// is exactly the kind of quiet weakening .claude/rules/loud-failures.md is about, so the
    /// run must name the package it could not hash. Against the throw-and-collapse code the
    /// message is the generic "dependency resolution failed" instead — which is also a false
    /// statement, since resolution succeeded.
    /// </summary>
    [SkippableFact]
    public void UnhashableDependency_IsReportedByName()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir, xPath, _) = ArrangeUnhashableX("loud");

        var run = RunPrintCacheKey(bundle, pkgDir, cacheDir);

        Assert.Contains("could not hash dependency package", run.Output);
        Assert.Contains(Path.GetFileName(xPath), run.Output);
    }

    // ── fixture writers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A one-codeunit bundle declaring TWO third-party dependencies and NO
    /// `application`/`platform`, so its resolved closure is entirely under this test's control.
    /// Two are required: the whole point is that a problem with one must not erase the other.
    /// </summary>
    private static string WriteTwoDepFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{BundleAppId}}",
          "name": "CacheKey Unhashable Dep Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{DepXId}}", "name": "Fabrikam Dep X", "publisher": "Fabrikam ISV", "version": "1.0.0.0" },
            { "id": "{{DepYId}}", "name": "Fabrikam Dep Y", "publisher": "Fabrikam ISV", "version": "1.0.0.0" }
          ],
          "idRanges": [ { "from": 60730, "to": 60739 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "CacheKeyUnhashable.Codeunit.al"), """
        codeunit 60730 "CacheKey Unhashable Probe"
        {
            procedure Probe(): Integer
            begin
                exit(1);
            end;
        }
        """);
        return dir;
    }

    private const string DepPublisher = "Fabrikam ISV";
    private const string DepVersion = "1.0.0.0";

    /// <summary>
    /// A minimal NAVX `.app` carrying a fixed-size uncompressed payload, so two packages with
    /// the same declared identity can differ in bytes. Every zip entry gets an explicit
    /// LastWriteTime — ZipArchive stamps DateTimeOffset.Now otherwise, which would make even a
    /// byte-for-byte rewrite produce different bytes and rob the control arm of its meaning.
    /// </summary>
    private static string WriteSyntheticApp(string dir, string appId, string name, byte payloadFill)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{DepPublisher}" Version="{DepVersion}"/>
              <Dependencies />
            </Package>
            """;
        var entryStamp = new DateTimeOffset(2021, 6, 7, 8, 9, 10, TimeSpan.Zero);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = zip.CreateEntry("NavxManifest.xml", CompressionLevel.NoCompression);
            manifest.LastWriteTime = entryStamp;
            using (var es = manifest.Open())
                es.Write(Encoding.UTF8.GetBytes(xml));

            var payload = zip.CreateEntry("payload.bin", CompressionLevel.NoCompression);
            payload.LastWriteTime = entryStamp;
            using (var ps = payload.Open())
            {
                var buf = new byte[4096];
                buf.AsSpan().Fill(payloadFill);
                ps.Write(buf);
            }
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        var path = Path.Combine(dir, $"{DepPublisher}_{name}_{DepVersion}.app");
        File.WriteAllBytes(path, result);
        return path;
    }

    // ── runner invocation ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the bundle in `--print-cache-key` mode. The SAME <paramref name="cacheDir"/> is
    /// passed on every call in a given test on purpose: CacheRoots resolves `app-manifests`
    /// under it, and a warm manifest index is what lets a later run resolve a package it can no
    /// longer open. Returns the key (null if none was printed) alongside the full output, so a
    /// failure can say what the runner actually did.
    /// </summary>
    private static (string? Key, string Output) RunPrintCacheKey(string bundlePath, string packageCacheDir, string cacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundlePath}\"");
        args.Append($" --package-cache \"{packageCacheDir}\"");
        args.Append($" --cache \"{cacheDir}\"");
        args.Append(" --print-cache-key");
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
        string output;
        lock (sb) output = sb.ToString();
        var m = Regex.Match(output, @"\[cache\]\s+KEY\s+key=([0-9a-f]{64})");
        return (m.Success ? m.Groups[1].Value : null, output);
    }
}
