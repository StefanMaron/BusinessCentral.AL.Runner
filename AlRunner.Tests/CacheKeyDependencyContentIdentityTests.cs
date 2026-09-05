// CacheKeyDependencyContentIdentityTests — issue #2754: the AL-output cache key must identify
// each resolved dependency by its CONTENT, not by a filesystem stat of the winning package.
//
// The defect
// ----------
// `GetOrderedDepIds` (AlRunner/ProgramSupport/Dependencies.cs) used to write one term per
// resolved dependency of the form
//
//     {AppId:N}:{Version}:{LastWriteTimeUtc.Ticks}:{Length}
//
// and `ComputeAlCacheKey` folded those terms straight into the key. That is a *stat* identity,
// and it carries no path — so the identity of a dependency, across the whole filesystem, was
// (declared id, declared version, size, mtime). Two `.app` packages that declare the same
// publisher/name/version, are the same size, and carry the same mtime therefore hash to the
// same AL-output cache key even when their bytes differ, and a warm cache serves the DLL
// compiled against whichever one was seen first.
//
// Nothing about that fails: the run reports the same exit code and the same green tests, having
// executed code compiled against a dependency it never saw. Same defect family as the two the
// key's own comments already record — the `--define` symbols that were missing (making
// `--define` a silent no-op on a cache hit) and the dependency closure that was missing
// entirely (3175424 -> 3206144 bytes emitted, key unchanged).
//
// Reachability is not exotic. `cp -p`, `rsync -a`, `tar -x`, `unzip` and every CI cache restore
// carry an mtime along with the bytes, so equal mtimes across two directories is the normal
// case rather than a coincidence; a locally rebuilt ISV dependency keeps its declared version
// while its content changes; and `Program.cs` folds
// `ProvisioningCheck.CollectRunnerOwnedProvisionDirs(...)` into the package-cache search set,
// so which directories are searched depends on what happens to exist on the machine.
//
// The two arms
// ------------
// NEGATIVE (the collision) — two packages with the SAME declared id+version, the SAME byte
// length and the SAME mtime, differing only in their bytes. Every input the old stamp could
// see is identical, so the old key collides and the new key must not.
//
// POSITIVE (the cache must still hit) — the SAME bytes at a DIFFERENT path with a DIFFERENT
// mtime must produce the SAME key. Without this arm the negative one is satisfied by a key
// that keys on something always-unique, which would pass a collision-only test while silently
// destroying the cache's whole value. It also pins the direction the fix improves: under the
// old stamp a re-downloaded, byte-identical package MISSed unconditionally.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class CacheKeyDependencyContentIdentityTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    // Distinct from CacheKeyDependencyClosureTests' IsvDepId on purpose: these tests run in a
    // separate collection, in parallel with that one, and both write synthetic packages under
    // their own scratch roots. A shared AppId would still not cross directories, but a distinct
    // one keeps a failure message unambiguous about which fixture produced it.
    private const string DepAppId = "c40e2754-9a11-4b22-8c33-d44455566677";
    private const string BundleAppId = "c40e2754-1122-4333-8444-555566667777";

    private readonly string _scratch;

    public CacheKeyDependencyContentIdentityTests()
    {
        _scratch = TestScratch.Dir("al-runner-cachekey-content");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>
    /// Two `.app` packages that declare the same id+version, are byte-for-byte the same LENGTH
    /// and carry the same MTIME, but hold different bytes, must not share an AL-output cache
    /// key. Under the old `mtime:length` stamp every term the key could see was identical and
    /// the two keys were equal — a warm cache then served the DLL compiled against the other
    /// package, green, with an unchanged exit code.
    /// </summary>
    [SkippableFact]
    public void SameVersionDifferentBytes_ProducesDifferentCacheKey()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteDependentFixture(Path.Combine(_scratch, "bundle-collide"));
        var dirA = Path.Combine(_scratch, "pkg-a");
        var dirB = Path.Combine(_scratch, "pkg-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        // Same declared identity, same payload SIZE, different payload BYTES.
        var appA = WriteSyntheticApp(dirA, payloadFill: (byte)'A');
        var appB = WriteSyntheticApp(dirB, payloadFill: (byte)'B');

        // Same mtime, to the tick. Every filesystem-stat term the old key could observe is now
        // provably identical across the two packages.
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(appA, stamp);
        File.SetLastWriteTimeUtc(appB, stamp);

        var bytesA = File.ReadAllBytes(appA);
        var bytesB = File.ReadAllBytes(appB);

        // Preconditions, asserted rather than assumed: without all three this test would not be
        // exercising the defect at all, and a silent drift in the synthetic-package writer
        // (a zip timestamp, a compression-level change) would turn it into a test that passes
        // for the wrong reason.
        Assert.Equal(bytesA.Length, bytesB.Length);
        Assert.Equal(
            new FileInfo(appA).LastWriteTimeUtc.Ticks,
            new FileInfo(appB).LastWriteTimeUtc.Ticks);
        Assert.False(bytesA.AsSpan().SequenceEqual(bytesB),
            "the two synthetic packages are byte-identical — the fixture is not exercising the defect");

        var keyA = ReadCacheKey(bundle, dirA, Path.Combine(_scratch, "cache-a"));
        var keyB = ReadCacheKey(bundle, dirB, Path.Combine(_scratch, "cache-b"));

        Assert.NotEqual(keyA, keyB);
    }

    /// <summary>
    /// The direction that keeps the fix honest: byte-identical dependency packages must key
    /// IDENTICALLY even at a different path and a different mtime. A key that simply varied per
    /// resolved file would satisfy the collision test above while destroying every cache hit.
    ///
    /// This is also a strict improvement over the `mtime:length` stamp, which MISSed
    /// unconditionally for a package re-downloaded or re-copied with fresh timestamps — the
    /// #1815/#1820 finding, one cache layer over.
    /// </summary>
    [SkippableFact]
    public void SameBytesDifferentPathAndMtime_ProducesTheSameCacheKey()
    {
        TestArtifacts.SkipIfMissing();

        var bundle = WriteDependentFixture(Path.Combine(_scratch, "bundle-stable"));
        var dirA = Path.Combine(_scratch, "same-a");
        var dirB = Path.Combine(_scratch, "same-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        var appA = WriteSyntheticApp(dirA, payloadFill: (byte)'A');
        var appB = WriteSyntheticApp(dirB, payloadFill: (byte)'A');

        File.SetLastWriteTimeUtc(appA, new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(appB, new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc));

        // Preconditions again: the packages must genuinely be byte-identical and genuinely
        // differ in mtime, or this arm proves nothing.
        Assert.True(File.ReadAllBytes(appA).AsSpan().SequenceEqual(File.ReadAllBytes(appB)),
            "the two synthetic packages differ in bytes — the fixture is not exercising a cache HIT");
        Assert.NotEqual(
            new FileInfo(appA).LastWriteTimeUtc.Ticks,
            new FileInfo(appB).LastWriteTimeUtc.Ticks);

        var keyA = ReadCacheKey(bundle, dirA, Path.Combine(_scratch, "cache-same-a"));
        var keyB = ReadCacheKey(bundle, dirB, Path.Combine(_scratch, "cache-same-b"));

        Assert.Equal(keyA, keyB);
    }

    // ── fixture writers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A one-codeunit bundle declaring exactly one third-party dependency and NO
    /// `application`/`platform`, so its resolved closure is entirely under this test's control
    /// and no provisioning decision (nor the runner-owned provision dirs Program.cs folds into
    /// the package-cache search set) can contribute a package to it.
    /// </summary>
    private static string WriteDependentFixture(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "{{BundleAppId}}",
          "name": "CacheKey Content Identity Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{DepAppId}}", "name": "Fabrikam Dep", "publisher": "Fabrikam ISV", "version": "1.0.0.0" }
          ],
          "idRanges": [ { "from": 60710, "to": 60719 } ],
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(dir, "CacheKeyContent.Codeunit.al"), """
        codeunit 60710 "CacheKey Content Probe"
        {
            procedure Probe(): Integer
            begin
                exit(1);
            end;
        }
        """);
        return dir;
    }

    private const string DepName = "Fabrikam Dep";
    private const string DepPublisher = "Fabrikam ISV";
    private const string DepVersion = "1.0.0.0";

    /// <summary>
    /// Writes a minimal NAVX `.app` — enough for AppLoader.ReadManifest and DependencyResolver
    /// to see the package and its version — carrying a fixed-size uncompressed payload entry
    /// filled with <paramref name="payloadFill"/>.
    ///
    /// Two details are load-bearing and neither is incidental:
    /// <list type="bullet">
    /// <item><description>Every zip entry gets an EXPLICIT LastWriteTime. ZipArchive stamps
    /// DateTimeOffset.Now otherwise, which would make two "identical" packages differ in bytes
    /// and silently invert the positive arm below into a second collision test.</description></item>
    /// <item><description>The payload entry is stored with CompressionLevel.NoCompression, so
    /// its on-disk size is exactly the payload length regardless of how compressible the fill
    /// byte is — which is what makes the two packages the same LENGTH while differing in
    /// content, the precise shape the old mtime:length stamp could not tell apart.</description></item>
    /// </list>
    /// </summary>
    private static string WriteSyntheticApp(string dir, byte payloadFill)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{DepAppId}" Name="{DepName}" Publisher="{DepPublisher}" Version="{DepVersion}"/>
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
        var path = Path.Combine(dir, $"{DepPublisher}_{DepName}_{DepVersion}.app");
        File.WriteAllBytes(path, result);
        return path;
    }

    // ── runner invocation ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the bundle in `--print-cache-key` mode, which reaches the exact ComputeAlCacheKey
    /// call a real run would (see Program.cs) and exits before Emit+Compile — the same cheap
    /// path CacheKeyDependencyClosureTests uses, anchored to a real run by that class's
    /// PrintCacheKeyOnly_MatchesFullRunKey.
    /// </summary>
    private static string ReadCacheKey(string bundlePath, string packageCacheDir, string alCacheDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundlePath}\"");
        args.Append($" --package-cache \"{packageCacheDir}\"");
        args.Append($" --cache \"{alCacheDir}\"");
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
        Assert.True(m.Success, $"could not read a cache key from --print-cache-key output:\n{output}");
        return m.Groups[1].Value;
    }
}
