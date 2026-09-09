// AppLoaderNeaRuntimePackageTests — AppLoader reads a runtime package (#3537).
//
// A runtime package is a `.app` whose NAVX payload is a `.NEA` container rather than a bare ZIP,
// so every one of AppLoader's readers used to answer wrongly about one. Two failure modes, and
// the silent one is the worse:
//
//   ExtractCSharp / ExtractAl  threw InvalidDataException("End of Central Directory record
//                              could not be found") — the ZIP reader hitting RC4 bytes.
//   IsR2R / HasAlSource        answered `false` through `catch { return false; }`, which reads
//                              as "this package carries no implementation" and drops it from
//                              DependencyResolver's scan set with no diagnosis at all.
//
// The format is fixed and carries no secret: `.NEA` + a big-endian version, then RC4 with a
// 6-byte key that ships in Microsoft.Dynamics.Nav.CodeAnalysis as
// `Packaging.Nea.NeaStream.Key`. Under that one layer is an ordinary ZIP, which is why peeling
// it is all this takes. Measured identical on three genuine third-party runtime packages of one
// app built by three different BC compilers (27.5.46862, 28.1.49838, 28.4.53241): same header,
// same key, 26 `/bin/*.xml` + 18 `/bin/*.cs` + `SymbolReference.json` + zero AL source in each.
// docs/runtime-packages.md has the layout and the measurement.
using System.IO.Compression;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class AppLoaderNeaRuntimePackageTests
{
    private static string NewTempDir(string suffix)
    {
        var dir = TestScratch.FlatDir("app-loader-nea-tests-" + suffix + "-");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string ManifestXml(Guid appId, string name) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
          <App Id="{appId}" Name="{name}" Publisher="Pub" Version="2.3.4.5"
               Application="28.0.0.0" Platform="28.0.0.0"/>
          <Dependencies/>
        </Package>
        """;

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

    /// <summary>
    /// The `.NEA` obfuscation BC's own writer applies, reproduced here so the fixture is a real
    /// runtime package rather than a mock of one. Independent of the production decoder on
    /// purpose: a test that encoded with the same code it decodes with would pass even if both
    /// halves were wrong together.
    /// </summary>
    private static byte[] NeaEncode(byte[] plain)
    {
        byte[] key = [0x0F, 0x0B, 0x51, 0x89, 0xB8, 0x78];
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var body = new byte[plain.Length];
        for (int n = 0, i = 0, j = 0; n < plain.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            body[n] = (byte)(plain[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        byte[] header = [0x2E, 0x4E, 0x45, 0x41, 0x00, 0x00, 0x00, 0x01];
        var result = new byte[header.Length + body.Length];
        header.CopyTo(result, 0);
        body.CopyTo(result, header.Length);
        return result;
    }

    /// <summary>The 40-byte NAVX header BC writes, followed by the payload at offset 40.</summary>
    private static byte[] Navx(byte[] payload, Guid appId)
    {
        var result = new byte[40 + payload.Length];
        "NAVX"u8.CopyTo(result);
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), 40u);
        BitConverter.TryWriteBytes(result.AsSpan(8, 4), 2u);
        appId.ToByteArray().CopyTo(result, 12);
        BitConverter.TryWriteBytes(result.AsSpan(28, 8), (long)payload.Length);
        "NAVX"u8.CopyTo(result.AsSpan(36, 4));
        payload.CopyTo(result, 40);
        return result;
    }

    private const string TableMetadataXml =
        """<MetaTable MetadataVersion="130000" ID="60001" Name="Probe Table"/>""";
    private const string GeneratedCSharp =
        "namespace Probe { public sealed class Table60001 { public int Answer() { return 42; } } }";

    /// <summary>Writes a runtime package: NAVX → .NEA → ZIP with /bin content and no AL source.</summary>
    private static string WriteRuntimePackage(string dir, Guid appId, string name)
    {
        var zip = ZipWith(
            ("NavxManifest.xml", Utf8(ManifestXml(appId, name))),
            ("SymbolReference.json", Utf8("""{"AppId":"probe"}""")),
            ("bin/TAB60001.xml", Utf8(TableMetadataXml)),
            ("bin/TAB60001.cs", Utf8(GeneratedCSharp)));
        var path = Path.Combine(dir, name + ".app");
        File.WriteAllBytes(path, Navx(NeaEncode(zip), appId));
        return path;
    }

    private static readonly byte[] FontBytes = Utf8("PROBE-FONT-BYTES-v1");

    /// <summary>
    /// A package carrying a <c>/resources/</c> part, written either plain or `.NEA`-wrapped from
    /// the identical ZIP. NavAppResourcePatches reads this part to answer NavApp.GetResource, and
    /// it is on the dependency path (DependencyLoader sets AppPath from a resolved dependency
    /// .app), so a runtime package must answer it the same way an ordinary one does.
    /// </summary>
    private static string WriteResourcePackage(string dir, Guid appId, string name, bool nea)
    {
        var zip = ZipWith(
            ("NavxManifest.xml", Utf8(ManifestXml(appId, name))),
            ("PackagedResources.json", Utf8("""{"Resources":["fonts/Probe.ttf"]}""")),
            ("resources/fonts/Probe.ttf", FontBytes));
        var path = Path.Combine(dir, name + ".app");
        File.WriteAllBytes(path, Navx(nea ? NeaEncode(zip) : zip, appId));
        return path;
    }

    /// <summary>The same content as an ordinary package, so each assertion has a control.</summary>
    private static string WritePlainPackage(string dir, Guid appId, string name)
    {
        var zip = ZipWith(
            ("NavxManifest.xml", Utf8(ManifestXml(appId, name))),
            ("SymbolReference.json", Utf8("""{"AppId":"probe"}""")),
            ("bin/TAB60001.xml", Utf8(TableMetadataXml)),
            ("bin/TAB60001.cs", Utf8(GeneratedCSharp)));
        var path = Path.Combine(dir, name + ".app");
        File.WriteAllBytes(path, Navx(zip, appId));
        return path;
    }

    [Fact]
    public void ReadManifest_AnswersTheRealIdentityOfANeaRuntimePackage()
    {
        var dir = NewTempDir("manifest");
        var appId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var path = WriteRuntimePackage(dir, appId, "RuntimePkg");

        AppLoader.ResetManifestMemoForTests();
        var manifest = AppLoader.ReadManifest(path);

        Assert.NotNull(manifest);
        Assert.Equal("RuntimePkg", manifest!.Name);
        Assert.Equal("Pub", manifest.Publisher);
        Assert.Equal(new Version(2, 3, 4, 5), manifest.Version);
        Assert.Equal(appId, manifest.AppId);
    }

    [Fact]
    public void ExtractCSharp_ReturnsTheGeneratedCodeShippedInTheRuntimePackage()
    {
        var dir = NewTempDir("cs");
        var path = WriteRuntimePackage(dir, Guid.NewGuid(), "RuntimePkgCs");

        var sources = AppLoader.ExtractCSharp(path);

        var one = Assert.Single(sources);
        Assert.Contains("Table60001", one.Code);
        Assert.Contains("return 42;", one.Code);
    }

    [Fact]
    public void HasSymbolReference_IsTrueForARuntimePackageThatShipsOne()
    {
        var dir = NewTempDir("symref");
        var path = WriteRuntimePackage(dir, Guid.NewGuid(), "RuntimePkgSym");

        Assert.True(AppLoader.HasSymbolReference(path));
    }

    /// <summary>
    /// The silent half of the defect. A runtime package genuinely ships no AL source, so `false`
    /// is the right ANSWER — but before the fix it was reached by swallowing an
    /// InvalidDataException, which returned the same `false` for a package the runner could not
    /// read at all. Pinning the reachable-and-correct path is what stops that from regressing
    /// into an unreadable package being reported as a readable one with nothing in it.
    /// </summary>
    [Fact]
    public void HasAlSource_IsFalseForARuntimePackageBecauseItShipsNoneNotBecauseItIsUnreadable()
    {
        var dir = NewTempDir("alsrc");
        var path = WriteRuntimePackage(dir, Guid.NewGuid(), "RuntimePkgNoAl");

        Assert.False(AppLoader.HasAlSource(path));
        // Readable: the same package answers every other question truthfully.
        Assert.True(AppLoader.HasSymbolReference(path));
        Assert.Single(AppLoader.ExtractCSharp(path));
        Assert.Empty(AppLoader.ExtractAl(path));
    }

    [Fact]
    public void IsR2R_IsFalseForARuntimePackageWhichCarriesNoPublishedArtifacts()
    {
        var dir = NewTempDir("r2r");
        var path = WriteRuntimePackage(dir, Guid.NewGuid(), "RuntimePkgNoR2r");

        Assert.False(AppLoader.IsR2R(path));
    }

    /// <summary>
    /// Equivalence: wrapping the identical ZIP in `.NEA` must change no answer AppLoader gives.
    /// Without this, a decoder that silently produced different content would still pass every
    /// assertion above.
    /// </summary>
    [Fact]
    public void ANeaPackageAndAPlainPackageWithTheSameContentAnswerIdentically()
    {
        var dir = NewTempDir("equiv");
        var appId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
        var nea = WriteRuntimePackage(dir, appId, "EquivNea");
        var plain = WritePlainPackage(dir, appId, "EquivPlain");

        AppLoader.ResetManifestMemoForTests();
        var mNea = AppLoader.ReadManifest(nea);
        var mPlain = AppLoader.ReadManifest(plain);

        Assert.NotNull(mNea);
        Assert.NotNull(mPlain);
        Assert.Equal(mPlain!.AppId, mNea!.AppId);
        Assert.Equal(mPlain.Publisher, mNea.Publisher);
        Assert.Equal(mPlain.Version, mNea.Version);
        Assert.Equal(AppLoader.HasSymbolReference(plain), AppLoader.HasSymbolReference(nea));
        Assert.Equal(AppLoader.IsR2R(plain), AppLoader.IsR2R(nea));
        Assert.Equal(AppLoader.HasAlSource(plain), AppLoader.HasAlSource(nea));
        Assert.Equal(
            AppLoader.ExtractCSharp(plain).Select(s => s.Code).ToArray(),
            AppLoader.ExtractCSharp(nea).Select(s => s.Code).ToArray());
    }

    /// <summary>
    /// The negative direction: a file that begins with the `.NEA` magic but whose body is not a
    /// decodable ZIP must NOT be reported as an empty-but-fine package. `ReadManifest` returning
    /// null is how AppLoader says "unreadable", and ReportUnreadablePackage prints the diagnosis.
    /// </summary>
    [Fact]
    public void ACorruptNeaPayloadIsReportedUnreadableRatherThanAsAnEmptyPackage()
    {
        var dir = NewTempDir("corrupt");
        byte[] header = [0x2E, 0x4E, 0x45, 0x41, 0x00, 0x00, 0x00, 0x01];
        var garbage = new byte[256];
        new Random(1234).NextBytes(garbage);
        var payload = new byte[header.Length + garbage.Length];
        header.CopyTo(payload, 0);
        garbage.CopyTo(payload, header.Length);
        var path = Path.Combine(dir, "Corrupt.app");
        File.WriteAllBytes(path, Navx(payload, Guid.NewGuid()));

        AppLoader.ResetManifestMemoForTests();
        Assert.Null(AppLoader.ReadManifest(path));
        Assert.False(AppLoader.HasSymbolReference(path));
    }

    /// <summary>
    /// The THIRD reader copy (#3537 review). NavAppResourcePatches opened a ZipArchive over the
    /// raw NAVX payload through its own private NavxZipOffset, so it could not see through a
    /// `.NEA` container — and the failure was silent in the shape loud-failures.md names: the
    /// InvalidDataException was caught, logged to Console.Error (off the visible path, since the
    /// runner re-execs itself), and an EMPTY dictionary returned as if complete. A runtime
    /// package's resources then reported as absent from the app rather than as unreadable.
    ///
    /// Asserting a specific count and the specific bytes is what makes this prove something: a
    /// test that only checked "no throw" would have passed against the broken reader, which
    /// returned zero resources without throwing.
    /// </summary>
    [Fact]
    public void PackageResources_AreReadFromANeaRuntimePackageNotReportedAbsent()
    {
        var dir = NewTempDir("res-nea");
        var path = WriteResourcePackage(dir, Guid.NewGuid(), "ResNea", nea: true);

        var resources = Patches.NavAppResourcePatches.ReadPackageResourcesForTests(path);

        var one = Assert.Single(resources);
        Assert.Equal("fonts/Probe.ttf", one.Key);
        Assert.Equal(FontBytes, one.Value);
    }

    /// <summary>
    /// The equivalence control for the same reader: identical ZIP content, one plain and one
    /// `.NEA`, must produce identical resource dictionaries. Without this a decoder that produced
    /// silently different bytes would still satisfy the count assertion above.
    /// </summary>
    [Fact]
    public void PackageResources_AnswerIdenticallyForANeaAndAPlainPackageWithTheSameContent()
    {
        var dir = NewTempDir("res-equiv");
        var appId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        var nea = WriteResourcePackage(dir, appId, "ResEquivNea", nea: true);
        var plain = WriteResourcePackage(dir, appId, "ResEquivPlain", nea: false);

        var fromNea = Patches.NavAppResourcePatches.ReadPackageResourcesForTests(nea);
        var fromPlain = Patches.NavAppResourcePatches.ReadPackageResourcesForTests(plain);

        Assert.Single(fromPlain);          // the control is itself non-empty
        Assert.Equal(fromPlain.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     fromNea.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (name, bytes) in fromPlain)
            Assert.Equal(bytes, fromNea[name]);
    }
}
