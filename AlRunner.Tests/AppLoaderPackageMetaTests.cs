// AppLoaderPackageMetaTests — AppLoader.ReadPackageMeta answers both metadata questions about a
// .app from one read, and records the answer in the same on-disk index ReadManifest uses
// (issue #2607).
//
// Two things have to be true and they pull in opposite directions.
//
// Equivalence: the pair it returns must be exactly what a separate ReadManifest plus
// HasSymbolReference used to return, for every package shape — flat, R2R-nested, and the awkward
// one where the outer archive carries NavxManifest.xml while only the NESTED archive carries
// SymbolReference.json. Getting that last one wrong reports false, false drops the package from
// the compile's scan set, and the resulting AL1023 is attributed to the whole compilation rather
// than to the package.
//
// Compatibility: an index entry written before the flag existed has no flag, and "no flag" must
// mean "go and look", never false — otherwise the first run after an upgrade drops packages on
// exactly the machines that have a warm cache.
using System.IO.Compression;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(CacheRootsSerialCollection.Name)]
public sealed class AppLoaderPackageMetaTests
{
    private static string NewTempDir(string suffix)
    {
        var dir = TestScratch.FlatDir("app-loader-package-meta-tests-" + suffix + "-");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ManifestXml(Guid appId, string name) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
          <App Id="{appId}" Name="{name}" Publisher="Pub" Version="1.0.0.0"
               Application="27.0.0.0" Platform="27.0.0.0"/>
          <Dependencies/>
        </Package>
        """;

    /// <summary>NAVX wrapper: magic "NAVX" + little-endian uint32 ZIP offset (8) + ZIP bytes.</summary>
    private static byte[] Navx(byte[] zipBytes)
    {
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    private static byte[] ZipWith(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A flat package: manifest at the top level, symbol reference present or not.</summary>
    private static string WriteFlatApp(string dir, string fileName, Guid appId, string name, bool withSymbolReference)
    {
        var entries = new List<(string, byte[])> { ("NavxManifest.xml", Utf8(ManifestXml(appId, name))) };
        if (withSymbolReference) entries.Add(("SymbolReference.json", Utf8("{}")));
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, Navx(ZipWith(entries.ToArray())));
        return path;
    }

    /// <summary>
    /// An R2R-shaped package: an outer archive holding a nested <c>.app</c>, plus whatever the
    /// caller wants at the outer level. Microsoft ships this shape, and it is the reason the two
    /// questions used to cost two reads of the same package.
    /// </summary>
    private static string WriteNestedApp(
        string dir, string fileName, Guid appId, string name,
        bool outerManifest, bool outerSymbolReference, bool innerSymbolReference)
    {
        var innerEntries = new List<(string, byte[])> { ("NavxManifest.xml", Utf8(ManifestXml(appId, name))) };
        if (innerSymbolReference) innerEntries.Add(("SymbolReference.json", Utf8("{}")));
        var inner = Navx(ZipWith(innerEntries.ToArray()));

        var outerEntries = new List<(string, byte[])> { ("Inner.app", inner) };
        if (outerManifest) outerEntries.Add(("NavxManifest.xml", Utf8(ManifestXml(appId, name))));
        if (outerSymbolReference) outerEntries.Add(("SymbolReference.json", Utf8("{}")));

        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, Navx(ZipWith(outerEntries.ToArray())));
        return path;
    }

    // ---- equivalence, shape by shape --------------------------------------------

    public static TheoryData<string, bool> PackageShapes => new()
    {
        { "flat-with", true },
        { "flat-without", false },
        { "nested-inner-symref", true },
        { "nested-outer-manifest-inner-symref", true },
        { "nested-no-symref", false },
    };

    private static string WriteShape(string dir, string shape, Guid appId) => shape switch
    {
        "flat-with" => WriteFlatApp(dir, "a.app", appId, "Flat With", withSymbolReference: true),
        "flat-without" => WriteFlatApp(dir, "a.app", appId, "Flat Without", withSymbolReference: false),
        "nested-inner-symref" => WriteNestedApp(dir, "a.app", appId, "Nested",
            outerManifest: false, outerSymbolReference: false, innerSymbolReference: true),
        // The dangerous one: everything the outer archive can answer says "no symbol reference",
        // and the truth is one level down.
        "nested-outer-manifest-inner-symref" => WriteNestedApp(dir, "a.app", appId, "Nested Outer Manifest",
            outerManifest: true, outerSymbolReference: false, innerSymbolReference: true),
        "nested-no-symref" => WriteNestedApp(dir, "a.app", appId, "Nested No Symref",
            outerManifest: false, outerSymbolReference: false, innerSymbolReference: false),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    [Theory]
    [MemberData(nameof(PackageShapes))]
    public void ReadPackageMeta_AnswersBothQuestions_ForEveryPackageShape(string shape, bool expectedSymbolReference)
    {
        var cacheRoot = NewTempDir("cache");
        var srcDir = NewTempDir("src");
        CacheRoots.SetOverride(cacheRoot);
        try
        {
            AppLoader.ResetManifestMemoForTests();
            var appId = Guid.NewGuid();
            var appPath = WriteShape(srcDir, shape, appId);

            var (manifest, hasSymbolReference) = AppLoader.ReadPackageMeta(appPath);

            Assert.NotNull(manifest);
            Assert.Equal(appId, manifest!.AppId);
            Assert.Equal("Pub", manifest.Publisher);
            Assert.Equal(new Version(1, 0, 0, 0), manifest.Version);
            Assert.Equal(expectedSymbolReference, hasSymbolReference);

            // And the single-question entry points, which now route through the same read, agree.
            AppLoader.ResetManifestMemoForTests();
            Assert.Equal(expectedSymbolReference, AppLoader.HasSymbolReference(appPath));
            AppLoader.ResetManifestMemoForTests();
            Assert.Equal(appId, AppLoader.ReadManifest(appPath)!.AppId);
        }
        finally
        {
            CacheRoots.ResetForTests();
            try { Directory.Delete(cacheRoot, true); Directory.Delete(srcDir, true); } catch { }
        }
    }

    /// <summary>A path that does not exist answers "no manifest, no symbol reference" rather
    /// than throwing — the contract every caller of the old pair relied on.</summary>
    [Fact]
    public void ReadPackageMeta_MissingFile_AnswersNullAndFalse()
    {
        var srcDir = NewTempDir("src");
        try
        {
            var (manifest, hasSymbolReference) =
                AppLoader.ReadPackageMeta(Path.Combine(srcDir, "does-not-exist.app"));

            Assert.Null(manifest);
            Assert.False(hasSymbolReference);
        }
        finally { try { Directory.Delete(srcDir, true); } catch { } }
    }

    /// <summary>A file that is not a NAVX package at all answers the same way.</summary>
    [Fact]
    public void ReadPackageMeta_GarbageFile_AnswersNullAndFalse()
    {
        var srcDir = NewTempDir("src");
        try
        {
            var path = Path.Combine(srcDir, "garbage.app");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("this is not a package"));

            var (manifest, hasSymbolReference) = AppLoader.ReadPackageMeta(path);

            Assert.Null(manifest);
            Assert.False(hasSymbolReference);
        }
        finally { try { Directory.Delete(srcDir, true); } catch { } }
    }

    // ---- the cache the change exists for ----------------------------------------

    /// <summary>
    /// The point of the change: the second PROCESS to ask does not open the package. Simulated by
    /// clearing only the in-process memo, so the answer can only come from the on-disk index —
    /// and proven with the parse counter, because an implementation that reparses to the same
    /// answer is correct and is not the fix.
    /// </summary>
    [Fact]
    public void ReadPackageMeta_SecondProcess_IsServedFromTheIndexWithoutReopeningThePackage()
    {
        var cacheRoot = NewTempDir("cache");
        var srcDir = NewTempDir("src");
        CacheRoots.SetOverride(cacheRoot);
        try
        {
            AppLoader.ResetManifestMemoForTests();
            var appId = Guid.NewGuid();
            var appPath = WriteFlatApp(srcDir, "a.app", appId, "Cached", withSymbolReference: true);

            var cold = AppLoader.ReadPackageMeta(appPath);
            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(appPath));

            AppLoader.ResetManifestMemoForTests();
            var fromIndex = AppLoader.ReadPackageMeta(appPath);

            Assert.Equal(1, AppLoader.ManifestParseInvocationCountForTests(appPath));
            Assert.Equal(cold.HasSymbolReference, fromIndex.HasSymbolReference);
            Assert.True(fromIndex.HasSymbolReference);
            Assert.Equal(appId, fromIndex.Manifest!.AppId);
        }
        finally
        {
            CacheRoots.ResetForTests();
            try { Directory.Delete(cacheRoot, true); Directory.Delete(srcDir, true); } catch { }
        }
    }

    /// <summary>
    /// The compatibility case. A machine upgrading to this build has an index full of entries
    /// written by ReadManifest alone, which record no flag. Absent must mean "go and look": read
    /// as false, every such package would be dropped as unserveable and the compile would fail
    /// with AL1023 for reasons entirely outside the AL being compiled.
    /// </summary>
    [Fact]
    public void ReadPackageMeta_IndexEntryWithoutTheFlag_IsRecomputedRatherThanReadAsFalse()
    {
        var cacheRoot = NewTempDir("cache");
        var srcDir = NewTempDir("src");
        CacheRoots.SetOverride(cacheRoot);
        try
        {
            AppLoader.ResetManifestMemoForTests();
            var appId = Guid.NewGuid();
            var appPath = WriteFlatApp(srcDir, "a.app", appId, "Legacy Entry", withSymbolReference: true);

            // ReadManifest writes an entry carrying the manifest and no flag — exactly the shape
            // every entry on disk has today.
            Assert.NotNull(AppLoader.ReadManifest(appPath));
            AppLoader.ResetManifestMemoForTests();

            var (manifest, hasSymbolReference) = AppLoader.ReadPackageMeta(appPath);

            Assert.True(hasSymbolReference,
                "a pre-existing index entry with no recorded flag must be recomputed, not answered false");
            Assert.Equal(appId, manifest!.AppId);

            // And the entry is upgraded, so only that one recomputation is paid.
            var parsesSoFar = AppLoader.ManifestParseInvocationCountForTests(appPath);
            AppLoader.ResetManifestMemoForTests();
            Assert.True(AppLoader.ReadPackageMeta(appPath).HasSymbolReference);
            Assert.Equal(parsesSoFar, AppLoader.ManifestParseInvocationCountForTests(appPath));
        }
        finally
        {
            CacheRoots.ResetForTests();
            try { Directory.Delete(cacheRoot, true); Directory.Delete(srcDir, true); } catch { }
        }
    }

    /// <summary>
    /// The other direction of the same key: a package rewritten in place — a `--bc-version`
    /// switch, a re-downloaded artifact, InProcessAppPackager rebuilding a synthetic .app — must
    /// be re-read, not served from the entry describing the bytes that used to be there.
    /// </summary>
    [Fact]
    public void ReadPackageMeta_RewrittenPackage_IsReReadRatherThanServedStale()
    {
        var cacheRoot = NewTempDir("cache");
        var srcDir = NewTempDir("src");
        CacheRoots.SetOverride(cacheRoot);
        try
        {
            AppLoader.ResetManifestMemoForTests();
            var firstId = Guid.NewGuid();
            var appPath = WriteFlatApp(srcDir, "a.app", firstId, "Before", withSymbolReference: false);
            Assert.False(AppLoader.ReadPackageMeta(appPath).HasSymbolReference);

            var secondId = Guid.NewGuid();
            WriteFlatApp(srcDir, "a.app", secondId, "After", withSymbolReference: true);
            File.SetLastWriteTimeUtc(appPath, DateTime.UtcNow.AddSeconds(5));
            AppLoader.ResetManifestMemoForTests();

            var (manifest, hasSymbolReference) = AppLoader.ReadPackageMeta(appPath);

            Assert.True(hasSymbolReference);
            Assert.Equal(secondId, manifest!.AppId);
        }
        finally
        {
            CacheRoots.ResetForTests();
            try { Directory.Delete(cacheRoot, true); Directory.Delete(srcDir, true); } catch { }
        }
    }
}
