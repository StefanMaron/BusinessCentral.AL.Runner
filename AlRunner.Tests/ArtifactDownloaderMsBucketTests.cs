// Issue #2724: a manual-dispatch workflow that runs one Microsoft BaseApp test bucket with
// --test-data needs three things the existing provisioning did not fetch — the bucket's
// `<bucket>.Source.zip` (platform artifact, Applications/BaseApp/Test/), the sandbox
// `BusinessCentral-<CC>.bak` (country artifact root, ~1 GB), and the `bcbak` reader. The first
// two ride on ArtifactDownloader's ranged-ZIP read; these tests pin the pure, no-network
// pieces of that: the two entry selectors, the Source.zip unpack, and the streaming entry
// copy the .bak needs because it does not fit the byte[]-returning ExtractEntry (977 MB
// uncompressed on BC 28.4, measured against the live CDN — see the PR).
//
// Both directions on every piece, per .claude/rules/tdd.md: the selector that accepts the
// real entry names also rejects the .app beside them, a prefix-collision bucket, and the
// Source/ (not Test/) sibling; the unpack that lands app.json also refuses a traversal
// entry BEFORE writing anything and a zip with no root app.json; the streaming copy that
// inflates a deflate entry byte-for-byte also names a truncated stream and an unsupported
// method instead of writing a short file that a later --test-data run would open and trust.
using System.IO.Compression;
using AlRunner.Provisioning;
using Xunit;

namespace AlRunner.Tests;

public sealed class ArtifactDownloaderMsBucketTests
{
    // ---- Source.zip selection -------------------------------------------------------

    [Theory]
    [InlineData("Applications/BaseApp/Test/Tests-ERM.Source.zip", "Tests-ERM")]
    [InlineData("applications/baseapp/test/tests-erm.source.zip", "Tests-ERM")]
    [InlineData("Applications\\BaseApp\\Test\\Tests-ERM.Source.zip", "tests-erm")]
    // Bucket names with spaces are real: Tests-Cash Flow, Tests-Cost Accounting, ...
    [InlineData("Applications/BaseApp/Test/Tests-Cash Flow.Source.zip", "Tests-Cash Flow")]
    public void IsBaseAppTestSourceEntry_MatchesTheBucketsSourceZip(string entryName, string bucket)
        => Assert.True(ArtifactDownloader.IsBaseAppTestSourceEntry(entryName, bucket));

    [Theory]
    // A different bucket in the same folder.
    [InlineData("Applications/BaseApp/Test/Tests-Bank.Source.zip", "Tests-ERM")]
    // The compiled .app that sits beside the Source.zip — the thing `test-apps` fetches.
    [InlineData("Applications/BaseApp/Test/Microsoft_Tests-ERM_28.4.53241.54318.app", "Tests-ERM")]
    // Prefix collision: the basename must match EXACTLY, not start with the bucket.
    [InlineData("Applications/BaseApp/Test/Tests-ERM-Extra.Source.zip", "Tests-ERM")]
    [InlineData("Applications/BaseApp/Test/Tests-ERM.Source.zip", "Tests-ER")]
    // Source/, not Test/: the Base Application's own source, not a test bucket.
    [InlineData("Applications/BaseApp/Source/Base Application.Source.zip", "Base Application")]
    // Another app's Test/ folder — same layout, different app; the mode is BaseApp buckets.
    [InlineData("Applications/APIV1/Test/_Exclude_APIV1_ Tests.Source.zip", "_Exclude_APIV1_ Tests")]
    [InlineData("", "Tests-ERM")]
    public void IsBaseAppTestSourceEntry_RejectsEverythingElse(string entryName, string bucket)
        => Assert.False(ArtifactDownloader.IsBaseAppTestSourceEntry(entryName, bucket));

    // ---- .bak selection -----------------------------------------------------------------

    [Theory]
    [InlineData("BusinessCentral-W1.bak", "w1")]
    [InlineData("businesscentral-w1.bak", "W1")]
    [InlineData("BusinessCentral-US.bak", "us")]
    public void IsTestDataBackupEntry_MatchesTheRootLevelBackupForTheCountry(string entryName, string country)
        => Assert.True(ArtifactDownloader.IsTestDataBackupEntry(entryName, country));

    [Theory]
    // Wrong country.
    [InlineData("BusinessCentral-W1.bak", "us")]
    // Not at the artifact root: the measured w1 artifact ships exactly one, at the root.
    // Anchoring there is the same defence IsWantedPlatformAppEntry uses against a
    // same-basename lookalike in another folder.
    [InlineData("Backups/BusinessCentral-W1.bak", "w1")]
    [InlineData("Extensions/Microsoft_Base Application_28.4.53241.54318.app", "w1")]
    [InlineData("BusinessCentral-W1.bak.txt", "w1")]
    [InlineData("", "w1")]
    public void IsTestDataBackupEntry_RejectsOtherCountriesFoldersAndFiles(string entryName, string country)
        => Assert.False(ArtifactDownloader.IsTestDataBackupEntry(entryName, country));

    // ---- Source.zip unpack -----------------------------------------------------------------

    private static MemoryStream BuildZip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open());
                w.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static string TempDir()
    {
        var dir = TestScratch.FlatDir("al-runner-ms-bucket-");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void UnpackSourceZip_WritesAppJsonAndEveryAlFile_FlatLikeTheRealBucket()
    {
        // The real Tests-ERM.Source.zip is FLAT: app.json at the root beside 297 .al files
        // (measured on BC 28.4.53241.54318). Nothing to strip, nothing to rename.
        var dest = TempDir();
        try
        {
            using var zip = BuildZip(
                ("app.json", "{ \"name\": \"Tests-ERM\" }"),
                ("ERMTest.Codeunit.al", "codeunit 134000 \"ERM Test\" { }"),
                ("Sub/Helper.Codeunit.al", "codeunit 134001 Helper { }"));

            var written = ArtifactDownloader.UnpackSourceZip(zip, dest);

            Assert.Equal(3, written);
            Assert.Equal("{ \"name\": \"Tests-ERM\" }", File.ReadAllText(Path.Combine(dest, "app.json")));
            Assert.Equal("codeunit 134000 \"ERM Test\" { }", File.ReadAllText(Path.Combine(dest, "ERMTest.Codeunit.al")));
            Assert.True(File.Exists(Path.Combine(dest, "Sub", "Helper.Codeunit.al")));
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    [Fact]
    public void UnpackSourceZip_RefusesAPathTraversalEntry_BeforeWritingAnything()
    {
        var dest = TempDir();
        var outside = Path.Combine(Path.GetDirectoryName(dest)!, "escaped-" + Path.GetFileName(dest) + ".al");
        try
        {
            using var zip = BuildZip(
                ("app.json", "{}"),
                ("Good.Codeunit.al", "codeunit 1 G { }"),
                ("../" + Path.GetFileName(outside), "codeunit 2 Evil { }"));

            var ex = Assert.Throws<InvalidDataException>(() => ArtifactDownloader.UnpackSourceZip(zip, dest));

            Assert.Contains("escapes", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outside), "the traversal entry must not land outside the destination");
            // Validation runs over the whole directory first: not even the benign entries
            // before the bad one are written, so a half-unpacked bundle never looks complete.
            Assert.False(File.Exists(Path.Combine(dest, "app.json")));
            Assert.False(File.Exists(Path.Combine(dest, "Good.Codeunit.al")));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public void UnpackSourceZip_RefusesAZipWithNoRootAppJson()
    {
        // Without app.json at the root the runner would not see a bundle at all — it would
        // report zero tests, which is exactly the silent "green tick meaning nothing ran"
        // shape this workflow exists to avoid. Fail here, with the reason named.
        var dest = TempDir();
        try
        {
            using var zip = BuildZip(
                ("Tests-ERM/app.json", "{}"),
                ("Tests-ERM/A.Codeunit.al", "codeunit 1 A { }"));

            var ex = Assert.Throws<InvalidDataException>(() => ArtifactDownloader.UnpackSourceZip(zip, dest));

            Assert.Contains("app.json", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dest, recursive: true); }
    }

    // ---- streaming entry copy (the .bak path) ----------------------------------------------

    private static byte[] RandomBytes(int count)
    {
        var rng = new Random(2724);
        var buf = new byte[count];
        rng.NextBytes(buf);
        return buf;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            ds.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    [Fact]
    public void CopyZipEntryData_Deflate_InflatesByteForByte_AndReturnsTheLength()
    {
        var raw = RandomBytes(1_500_000);
        using var source = new MemoryStream(Deflate(raw));
        using var dest = new MemoryStream();

        var copied = ArtifactDownloader.CopyZipEntryData(source, method: 8, expectedUncompressedLength: raw.Length, dest);

        Assert.Equal(raw.Length, copied);
        Assert.Equal(raw, dest.ToArray());
    }

    [Fact]
    public void CopyZipEntryData_Stored_CopiesVerbatim()
    {
        var raw = RandomBytes(4096);
        using var source = new MemoryStream(raw);
        using var dest = new MemoryStream();

        var copied = ArtifactDownloader.CopyZipEntryData(source, method: 0, expectedUncompressedLength: raw.Length, dest);

        Assert.Equal(raw.Length, copied);
        Assert.Equal(raw, dest.ToArray());
    }

    [Fact]
    public void CopyZipEntryData_TruncatedSource_ThrowsNamingExpectedAndActual()
    {
        // A .bak that stopped short must never be reported as fetched: TestDataOptions keys
        // its baseline cache on (path, length, mtime) and would open a truncated file
        // without complaint. The length check is the guard.
        var raw = RandomBytes(200_000);
        var deflated = Deflate(raw);
        using var source = new MemoryStream(deflated, 0, deflated.Length / 2);
        using var dest = new MemoryStream();

        var ex = Assert.Throws<InvalidDataException>(() =>
            ArtifactDownloader.CopyZipEntryData(source, method: 8, expectedUncompressedLength: raw.Length, dest));

        Assert.Contains(raw.Length.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("expected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopyZipEntryData_UnsupportedMethod_ThrowsNamingTheMethod()
    {
        using var source = new MemoryStream(new byte[16]);
        using var dest = new MemoryStream();

        var ex = Assert.Throws<NotSupportedException>(() =>
            ArtifactDownloader.CopyZipEntryData(source, method: 12, expectedUncompressedLength: 16, dest));

        Assert.Contains("12", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, dest.Length);
    }
}
