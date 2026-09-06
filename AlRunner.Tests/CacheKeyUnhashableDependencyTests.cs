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
// "Resolved but unhashable" no longer exists end-to-end (#2987) — READ THIS FIRST
// -----------------------------------------------------------------------------
// This file used to construct that state and drive all three claims through the real runner.
// The construction was: DependencyResolver.EnsureIndexed calls AppLoader.ReadManifest on every
// .app it finds, and ReadManifest was backed by an on-disk index keyed by (full path, length,
// last-write-ticks) — which `chmod 000` changes none of. So one warm-up run with the package
// readable populated that index, and the next run resolved the package from the index WITHOUT
// opening it while ComputeAppContentHash, which must read the bytes, threw.
//
// #2987 keyed that index on the package's CONTENT, because a stat standing in for content in a
// PERSISTED, cross-process index is what let one package be served under another's identity.
// Identifying a package therefore now requires reading it, and the construction above is gone:
// an unreadable package is not indexed at all, so it is never resolved, so the hash is never
// reached. A package the resolver DID index is one whose hash it already computed and memoized
// under the same (path, length, mtime) key the dep term will use — so the degraded term cannot
// be reached for a resolved dependency even if the file becomes unreadable mid-run.
//
// That is also what closes #2954's residue ("unhashable on two consecutive runs while the
// content changes between them") without the "do not cache this run" signal that issue
// proposes: the state that signal existed to handle no longer occurs.
//
// What the arms are now
// ---------------------
// The two claims that still have teeth are pinned directly on ProgramSupport
// .DependencyContentTerm, which is `internal` for exactly this reason — a defensive branch no
// caller can reach still has to behave, and #2954's own review comment sets that bar ("driven
// by a test rather than shipped unexercised"):
//
//   NON-COLLAPSING — one unhashable package degrades ONE term. Two different unhashable
//   packages must not produce the same term, which is the property the throw-and-collapse
//   code destroyed: it replaced the whole list with `unresolved:<type>:<message>`, a string
//   deterministic across runs, so a warm cache served the DLL compiled against a DIFFERENT
//   dependency's earlier bytes.
//
//   LOUD — keying a dependency on something weaker than its content must SAY so, naming the
//   package (.claude/rules/loud-failures.md).
//
// And the end-to-end arm now pins the behaviour that REPLACED the old construction: a package
// which becomes unreadable after being indexed is skipped and NAMED, rather than resolved from
// an entry nothing can tie to the bytes on disk. Silently dropping it would produce "a required
// dependency package is missing" for a package sitting in the cache directory — the
// mysterious-missing-dependency shape #2206 is about.

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
    /// A bundle depending on X and Y, both packages written and resolvable, one warm-up run to
    /// populate the app-manifest index under the shared cache root, then X made unreadable.
    ///
    /// <para>Before #2987 the warm-up was what made X resolvable-but-unhashable. It now makes
    /// X resolvable only while it is readable, which is the point of the arm below.</para>
    /// </summary>
    private (string Bundle, string PkgDir, string CacheDir, string XPath, string YPath) ArrangeUnreadableX(string name)
    {
        var bundle = WriteTwoDepFixture(Path.Combine(_scratch, name + "-bundle"));
        var pkgDir = Path.Combine(_scratch, name + "-pkg");
        Directory.CreateDirectory(pkgDir);
        var cacheDir = Path.Combine(_scratch, name + "-cache");

        var xPath = WriteSyntheticApp(pkgDir, DepXId, "Fabrikam Dep X", payloadFill: (byte)'X');
        var yPath = WriteSyntheticApp(pkgDir, DepYId, "Fabrikam Dep Y", payloadFill: (byte)'1');

        var warm = RunPrintCacheKey(bundle, pkgDir, cacheDir);
        Assert.True(warm.Key != null, $"warm-up run produced no cache key:\n{warm.Output}");

        Skip.IfNot(TryMakeUnreadable(xPath),
            "cannot make a file unreadable here (running as root, or a filesystem that ignores "
            + "mode bits)");

        return (bundle, pkgDir, cacheDir, xPath, yPath);
    }

    /// <summary>
    /// THE END-TO-END ARM, rewritten for #2987. A dependency package that becomes unreadable
    /// after its manifest was indexed must be SKIPPED AND NAMED — not resolved from an index
    /// entry that a stat, and only a stat, ties to those bytes.
    ///
    /// <para>Both halves matter. Resolving it anyway is the defect #2987 removes: the entry
    /// carries the package's Publisher/Name/Version/AppId and its whole declared dependency
    /// list, so a stat collision serves one package's identity for another's bytes. Skipping it
    /// SILENTLY is the #2206 shape: the run fails with "a required dependency package is
    /// missing" while the package sits in the cache directory, and nothing connects the two.
    /// The warm-up run above proves the package WAS resolvable a moment earlier, so this arm
    /// cannot pass by the package having been broken all along.</para>
    /// </summary>
    [SkippableFact]
    public void DependencyUnreadableAfterIndexing_IsSkippedAndNamed_NotResolvedFromTheIndex()
    {
        TestArtifacts.SkipIfMissing();
        var (bundle, pkgDir, cacheDir, xPath, _) = ArrangeUnreadableX("unreadable");

        var run = RunPrintCacheKey(bundle, pkgDir, cacheDir);

        // Named, with the reason and the path — not silently dropped.
        Assert.Contains("[packages] cannot read", run.Output);
        Assert.Contains(xPath, run.Output);

        // And NOT served from the warm index entry: no cache key comes out of a run whose
        // closure could not be resolved. Before #2987 this run produced a key.
        Assert.True(run.Key == null,
            "a package whose bytes cannot be read was still resolved — the app-manifests index "
            + $"answered for content nothing verified:\n{run.Output}");
    }

    /// <summary>
    /// NON-COLLAPSING, pinned directly on the degraded term (#2987 made it unreachable through
    /// the runner — see this file's header). Two DIFFERENT unhashable packages must produce
    /// two DIFFERENT terms.
    ///
    /// <para>This is the property the throw-and-collapse code destroyed. It replaced the whole
    /// dependency list with one `unresolved:&lt;type&gt;:&lt;message&gt;` string — an exception
    /// type and message, both deterministic across runs — so two closures that differ only in
    /// another dependency's bytes keyed identically and a warm cache served the wrong DLL.
    /// Asserting the two terms differ, and that each names its own package, is what a
    /// per-dependency degradation means.</para>
    /// </summary>
    [SkippableFact]
    public void DependencyContentTerm_TwoUnhashablePackages_DegradeSeparately()
    {
        var dir = Path.Combine(_scratch, "term-separate");
        Directory.CreateDirectory(dir);
        var a = WriteSyntheticApp(dir, DepXId, "Fabrikam Dep X", payloadFill: (byte)'X');
        var b = WriteSyntheticApp(dir, DepYId, "Fabrikam Dep Y", payloadFill: (byte)'1');

        // Readable first: the term is the package's content hash, and the two differ. Without
        // this half the arm below would pass against an implementation that degraded
        // EVERYTHING, which is the opposite of what is being claimed.
        var hashedA = ProgramSupport.DependencyContentTerm(a);
        var hashedB = ProgramSupport.DependencyContentTerm(b);
        Assert.StartsWith("sha256:", hashedA);
        Assert.StartsWith("sha256:", hashedB);
        Assert.NotEqual(hashedA, hashedB);

        Skip.IfNot(TryMakeUnreadable(a) && TryMakeUnreadable(b),
            "cannot make a file unreadable here (running as root, or a filesystem that ignores "
            + "mode bits)");
        AlRunner.Infrastructure.RunnerFingerprint.ClearFileContentHashMemoForTests();

        var degradedA = ProgramSupport.DependencyContentTerm(a);
        var degradedB = ProgramSupport.DependencyContentTerm(b);

        Assert.StartsWith("unhashable:", degradedA);
        Assert.StartsWith("unhashable:", degradedB);
        // Each names its OWN package: the term carries the path, so one unhashable package
        // cannot stand in for another.
        Assert.Contains(Path.GetFullPath(a), degradedA);
        Assert.Contains(Path.GetFullPath(b), degradedB);
        Assert.NotEqual(degradedA, degradedB);
    }

    /// <summary>
    /// LOUD. Downgrading a dependency's cache identity from its content to a path+stat is the
    /// kind of quiet weakening .claude/rules/loud-failures.md exists for, so it must name the
    /// package it could not hash — and say what it fell back to.
    /// </summary>
    [SkippableFact]
    public void DependencyContentTerm_Unhashable_IsReportedByName()
    {
        var dir = Path.Combine(_scratch, "term-loud");
        Directory.CreateDirectory(dir);
        var x = WriteSyntheticApp(dir, DepXId, "Fabrikam Dep X", payloadFill: (byte)'X');

        Skip.IfNot(TryMakeUnreadable(x),
            "cannot make a file unreadable here (running as root, or a filesystem that ignores "
            + "mode bits)");
        AlRunner.Infrastructure.RunnerFingerprint.ClearFileContentHashMemoForTests();

        var stderr = new StringWriter();
        var previous = Console.Error;
        string term;
        try
        {
            Console.SetError(stderr);
            term = ProgramSupport.DependencyContentTerm(x);
        }
        finally { Console.SetError(previous); }

        Assert.StartsWith("unhashable:", term);
        var said = stderr.ToString();
        Assert.Contains("could not hash dependency package", said);
        Assert.Contains(Path.GetFullPath(x), said);
        // It must also say what it did INSTEAD — "could not hash" alone leaves the reader
        // unable to tell a weakened key from a failed run.
        Assert.Contains("keying it on path+stat instead", said);
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
