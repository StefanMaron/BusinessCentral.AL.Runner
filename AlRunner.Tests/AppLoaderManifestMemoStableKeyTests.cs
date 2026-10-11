// AppLoaderManifestMemoStableKeyTests - #5499.
//
// The in-process manifest memos (AppLoader.ReadManifest, AppLoader.ReadPackageMeta and the scan
// layer in front of them, BcCompiler.ReadAppMeta) were keyed on (path, length, mtime). A package
// rewritten IN PLACE to the same length with its mtime put back (cp -p, rsync -t --inplace, an
// extraction that normalises entry times) kept that key and was answered from the memo with the OLD
// dependency list. The key now carries ctime (FileIdentity.MemoStamp, #5409), which a writer cannot
// set back.
//
// Every rewrite below restores the mtime with `touch -r`, to the nanosecond. File.SetLastWriteTimeUtc
// keeps 100 ns ticks only, so the put-back time would differ from the original in the statx mtime
// field and a test built on it would pass on that alone, whatever ctime did (issue #5499 comment).
// Each arm asserts its premise: same inode, same length, same nanosecond mtime, a later ctime.

using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public sealed class AppLoaderManifestMemoStableKeyTests : IDisposable
{
    private static readonly Guid AppId = Guid.Parse("5499a000-0000-4000-8000-000000000001");
    private static readonly Guid MidId = Guid.Parse("5499a000-0000-4000-8000-000000000002");
    private static readonly Guid LeafId = Guid.Parse("5499a000-0000-4000-8000-000000000003");

    private readonly string _root;
    private readonly string _cacheRoot;

    public AppLoaderManifestMemoStableKeyTests()
    {
        _root = TestScratch.FlatDir("app-loader-memo-stable-key-");
        Directory.CreateDirectory(_root);
        // Helper files go here, so nothing but the package changes in the package's own directory:
        // a memo keyed on that directory's stamp (the wrong subject) would otherwise see it move.
        Directory.CreateDirectory(Path.Combine(_root, "aux"));
        _cacheRoot = TestScratch.FlatDir("app-loader-memo-stable-key-cache-");
        Directory.CreateDirectory(_cacheRoot);
        CacheRoots.SetOverride(_cacheRoot);
        AppLoader.ResetManifestMemoForTests();
    }

    public void Dispose()
    {
        CacheRoots.ResetForTests();
        AppLoader.ResetManifestMemoForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { }
    }

    // Declared before the first test on purpose: TestArtifactsGateTests' skip detector attributes a skip call to the
    // [Fact] declared above it, so after a plain [Fact] this helper would be read as that test being able to skip.
    private static void RequireLinuxStatx()
    {
        // Linux only, as FileIdentityTests is: a missing key or missing coreutils fails below rather than skipping.
        Skip.IfNot(OperatingSystem.IsLinux(), "ctime is only available through statx on Linux (FileIdentity header)");
    }

    // ── the defect, end to end, through each memo ──────────────────────────────────────────

    [SkippableFact]
    public void ReadManifest_RewrittenToTheSameLengthWithTheMtimePutBack_AnswersTheNewDependencies()
    {
        RequireLinuxStatx();
        var (v1, v2) = Versions();
        var path = Write("Contoso_Assert_1.0.0.0.app", v1);
        Assert.Equal("Mid", DependencyNames(AppLoader.ReadManifest(path)));

        RewriteInPlaceKeepingMtime(path, v2);

        Assert.Equal("Leaf", DependencyNames(AppLoader.ReadManifest(path)));
    }

    [SkippableFact]
    public void ReadPackageMeta_RewrittenToTheSameLengthWithTheMtimePutBack_AnswersTheNewDependenciesAndFlag()
    {
        RequireLinuxStatx();
        var (v1, v2) = Versions();
        var path = Write("Contoso_Assert_1.0.0.0.app", v1);
        var before = AppLoader.ReadPackageMeta(path);
        Assert.Equal("Mid", DependencyNames(before.Manifest));
        Assert.False(before.HasSymbolReference);

        RewriteInPlaceKeepingMtime(path, v2);

        var after = AppLoader.ReadPackageMeta(path);
        Assert.Equal("Leaf", DependencyNames(after.Manifest));
        Assert.True(after.HasSymbolReference);
    }

    /// <summary>The scan layer in front of ReadPackageMeta has a memo of its own. AppLoader's is cleared
    /// between the two reads so this arm can only be answered fresh if THAT memo notices the rewrite.</summary>
    [SkippableFact]
    public void ReadAppMeta_RewrittenToTheSameLengthWithTheMtimePutBack_AnswersTheNewDependenciesAndFlag()
    {
        RequireLinuxStatx();
        var (v1, v2) = Versions();
        var path = Write("Contoso_Assert_1.0.0.0.app", v1);
        var before = BcCompiler.ReadAppMeta(new FileInfo(path));
        Assert.Equal("Mid", DependencyNames(before.Manifest));
        Assert.False(before.HasSymbolReference);

        RewriteInPlaceKeepingMtime(path, v2);
        AppLoader.ResetManifestMemoForTests();

        var after = BcCompiler.ReadAppMeta(new FileInfo(path));
        Assert.Equal("Leaf", DependencyNames(after.Manifest));
        Assert.True(after.HasSymbolReference);
    }

    // ── the other direction: an unchanged package is still a memo hit ──────────────────────

    /// <summary>A read moves no timestamp, so ctime in the key costs an unchanged package nothing. Counted
    /// through the identity delegate, which only a call that missed the memo reaches.</summary>
    [Fact]
    public void ReadManifest_AnUnchangedPackage_IsAMemoHitTheSecondTime()
    {
        var (v1, _) = Versions();
        var path = Write("Contoso_Assert_1.0.0.0.app", v1);
        var identityCalls = 0;
        Func<string, string> identityOf = p => { identityCalls++; return AppLoader.ComputeManifestIndexIdentity(p); };

        Assert.Equal("Mid", DependencyNames(AppLoader.ReadManifestCore(path, identityOf)));
        Assert.Equal("Mid", DependencyNames(AppLoader.ReadManifestCore(path, identityOf)));

        Assert.Equal(1, identityCalls);
    }

    // ── the stamp's three answers (the probe is the seam for the two a filesystem will not give) ──

    /// <summary>statx answered but declined ctime: no stamp, so no memo, and a same-length rewrite whose
    /// mtime was put back to the tick is still answered fresh. A fallback to length and mtime would
    /// serve the old manifest here; this is the "never keyed weaker" rule FileIdentity.Probe states.</summary>
    [Fact]
    public void ReadManifest_WhenTheFilesystemDeclinesCtime_BypassesTheMemoAndAnswersTheRewrite()
    {
        var (v1, v2) = Versions();
        var path = Write("Contoso_Assert_1.0.0.0.app", v1);
        var identityCalls = 0;
        Func<string, string> identityOf = p => { identityCalls++; return AppLoader.ComputeManifestIndexIdentity(p); };
        Func<string, (string?, bool)> declined = _ => (null, true);

        Assert.Equal("Mid", DependencyNames(AppLoader.ReadManifestCore(path, identityOf, declined)));
        Assert.Equal("Mid", DependencyNames(AppLoader.ReadManifestCore(path, identityOf, declined)));
        Assert.Equal(2, identityCalls);   // not served from the memo, even though nothing changed

        var mtime = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, v2);
        File.SetLastWriteTimeUtc(path, mtime);
        Assert.Equal(v1.Length, new FileInfo(path).Length);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));

        Assert.Equal("Leaf", DependencyNames(AppLoader.ReadManifestCore(path, identityOf, declined)));
    }

    /// <summary>A platform with no statx at all keeps the documented length-and-mtime fallback (#5410):
    /// the memo still serves an unchanged package, and still notices a changed length.</summary>
    [Fact]
    public void ReadManifest_WhenThePlatformCannotAnswer_KeepsTheLengthAndMtimeFallback()
    {
        var (v1, _) = Versions();
        var path = Write("Contoso_Assert_1.0.0.0.app", v1);
        var identityCalls = 0;
        Func<string, string> identityOf = p => { identityCalls++; return AppLoader.ComputeManifestIndexIdentity(p); };
        Func<string, (string?, bool)> noAnswer = _ => (null, false);

        Assert.Equal("Mid", DependencyNames(AppLoader.ReadManifestCore(path, identityOf, noAnswer)));
        Assert.Equal("Mid", DependencyNames(AppLoader.ReadManifestCore(path, identityOf, noAnswer)));
        Assert.Equal(1, identityCalls);

        var longer = Build("Leaf", LeafId, withSymbolReference: false, pad: 7);
        Assert.NotEqual(v1.Length, longer.Length);   // the premise: the length is what the fallback key sees
        File.WriteAllBytes(path, longer);
        Assert.Equal("Leaf", DependencyNames(AppLoader.ReadManifestCore(path, identityOf, noAnswer)));
    }

    [Fact]
    public void MemoStamp_IsTheProbeKey_TheFallback_OrNothing()
    {
        var (v1, _) = Versions();
        var path = Path.GetFullPath(Write("Contoso_Assert_1.0.0.0.app", v1));

        Assert.Equal("ino|1:2:3|4|5.6|7.8", FileIdentity.MemoStamp(path, _ => ("ino|1:2:3|4|5.6|7.8", false)));
        Assert.Null(FileIdentity.MemoStamp(path, _ => (null, true)));
        Assert.Equal(
            new FileInfo(path).Length + "|" + File.GetLastWriteTimeUtc(path).Ticks,
            FileIdentity.MemoStamp(path, _ => (null, false)));
        Assert.Null(FileIdentity.MemoStamp(path + ".missing", _ => (null, false)));
    }

    // ── fixture ────────────────────────────────────────────────────────────────────────────

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string DependencyNames(AppManifest? m)
        => m == null ? "<null manifest>" : string.Join(",", m.Dependencies.Select(d => d.Name));

    /// <summary>Two packages of identical length that disagree on the dependency list and on whether they
    /// carry a SymbolReference.json. Stored without compression, so a package's length is a function of
    /// its manifest's length alone, and the shorter is padded with trailing spaces to match.</summary>
    private static (byte[] First, byte[] Second) Versions()
    {
        var a = Build("Mid", MidId, withSymbolReference: false, pad: 0);
        var b = Build("Leaf", LeafId, withSymbolReference: true, pad: 0);
        var target = Math.Max(a.Length, b.Length);
        a = Build("Mid", MidId, withSymbolReference: false, pad: target - a.Length);
        b = Build("Leaf", LeafId, withSymbolReference: true, pad: target - b.Length);
        Assert.Equal(a.Length, b.Length);
        Assert.NotEqual(a, b);
        return (a, b);
    }

    private static byte[] Build(string depName, Guid depId, bool withSymbolReference, int pad)
    {
        // CompatibilityId on every <Dependency>: BC 27.5 does not link a dependency edge without it.
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{AppId}" Name="Assert" Publisher="Contoso" Version="1.0.0.0" CompatibilityId="0.0.0.0"/>
              <Dependencies>
                <Dependency Id="{depId}" Name="{depName}" Publisher="Contoso" MinVersion="1.0.0.0" CompatibilityId="0.0.0.0"/>
              </Dependencies>
            </Package>
            """ + new string(' ', pad);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Put(zip, "NavxManifest.xml", xml);
            if (withSymbolReference) Put(zip, "SymbolReference.json", "{}");
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        "NAVX"u8.CopyTo(result);
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    private static void Put(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);   // the same bytes on every run
        using var s = entry.Open();
        s.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Writes <paramref name="bytes"/> over the file (same inode), then puts the mtime back to the
    /// nanosecond with the OS tool, and checks the premise the arm depends on.</summary>
    private void RewriteInPlaceKeepingMtime(string path, byte[] bytes)
    {
        Assert.NotNull(FileIdentity.Probe(Path.GetFullPath(path)).Key);
        var reference = Path.Combine(_root, "aux", "reference.mtime");
        File.WriteAllText(reference, "");
        Assert.NotNull(Os("touch", "-r", path, reference));
        var length = new FileInfo(path).Length;
        var inode = Os("stat", "-c", "%i", path);
        var mtime = Os("stat", "-c", "%.9Y", path);
        var ctime = Os("stat", "-c", "%.9Z", path);
        Assert.NotNull(mtime);
        Assert.NotNull(ctime);
        WaitUntilTheClockPasses(ctime!);

        File.WriteAllBytes(path, bytes);
        Assert.NotNull(Os("touch", "-m", "-r", reference, path));

        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(inode, Os("stat", "-c", "%i", path));
        Assert.Equal(mtime, Os("stat", "-c", "%.9Y", path));   // were mtime to differ, a test could pass on that alone
        Assert.NotEqual(ctime, Os("stat", "-c", "%.9Z", path));
    }

    /// <summary>The kernel stamps ctime with a clock that can be a few milliseconds coarse, so a rewrite
    /// straight after the first read could carry the same ctime. Writes a scratch file until its ctime
    /// differs from <paramref name="ctime"/>: from then on, now is later than that ctime.</summary>
    private void WaitUntilTheClockPasses(string ctime)
    {
        var scratch = Path.Combine(_root, "aux", "clock.tmp");
        for (var i = 0; i < 5000; i++)
        {
            File.WriteAllText(scratch, i.ToString());
            if (Os("stat", "-c", "%.9Z", scratch) != ctime) return;
        }
        Assert.Fail("the filesystem clock did not advance past the package's ctime");
    }

    private static string? Os(string exe, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        return p.ExitCode == 0 ? stdout.Trim() : null;
    }
}
