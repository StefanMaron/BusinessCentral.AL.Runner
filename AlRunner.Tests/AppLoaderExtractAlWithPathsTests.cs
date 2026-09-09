// AppLoaderExtractAlWithPathsTests — AppLoader.ExtractAlWithPaths returns a package's AL at its
// package-relative PATH, which is what makes a shipped app's source writable to disk as a
// compilation unit (issue #3549).
//
// Both differences from ExtractAl were measured, and both presented identically: AL0185
// "'X' is missing" for objects the app itself declares, which reads as a missing dependency
// rather than as source the compile never saw.
//
//   PATHS   System Application ships an `EmailOutbox.Page.al` AND an `EmailOutbox.Table.al`.
//           ExtractAl returns `entry.Name` — the base name — so writing that list to one
//           directory silently drops one of the pair.
//   SCOPE   ExtractAl also filters to `src/`. Whatever a package keeps outside `src/` is
//           therefore invisible to it, and an object referenced from there does not resolve.
//
// ExtractAl is deliberately unchanged: the Tier-3 source compile wants a flat list of object
// bodies and has worked on one for a long time. This is an additional reader, not a replacement.
using System.IO.Compression;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class AppLoaderExtractAlWithPathsTests
{
    private static byte[] Navx(byte[] zipBytes)
    {
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }

    private static byte[] ZipWith(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        return ms.ToArray();
    }

    private static string WritePackage(string suffix, params (string Name, string Content)[] entries)
    {
        var dir = TestScratch.FlatDir("extract-al-with-paths-" + suffix + "-");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Test.app");
        File.WriteAllBytes(path, Navx(ZipWith(entries)));
        return path;
    }

    /// <summary>
    /// The defect that motivated this reader: two objects whose file names differ only by the
    /// object-kind segment. Asserted as two DISTINCT paths with their own contents, because the
    /// symptom of getting it wrong is not an error — it is one file quietly overwriting the
    /// other and the lost object reported as "missing".
    /// </summary>
    [Fact]
    public void SameBaseNameInDifferentFiles_StaysTwoDistinctEntries()
    {
        var app = WritePackage("dup-base",
            ("NavxManifest.xml", "<Package><App Id=\"" + Guid.NewGuid() + "\" Name=\"N\" Publisher=\"P\" Version=\"1.0.0.0\"/></Package>"),
            ("src/Email/EmailOutbox.Page.al", "page 1 X {}"),
            ("src/Email/EmailOutbox.Table.al", "table 1 X {}"));

        var got = AlRunner.AppLoader.ExtractAlWithPaths(app);

        Assert.Equal(2, got.Count);
        Assert.Contains(got, e => e.Path == "src/Email/EmailOutbox.Page.al" && e.Source == "page 1 X {}");
        Assert.Contains(got, e => e.Path == "src/Email/EmailOutbox.Table.al" && e.Source == "table 1 X {}");

        // The contrast that makes the claim concrete. ExtractAl returns base names, so the two
        // files share a stem — write them by stem, as a flattening extractor does, and one
        // overwrites the other.
        var flat = AlRunner.AppLoader.ExtractAl(app);
        Assert.Equal(2, flat.Count);
        var stems = flat
            .Select(e => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(e.Name)))
            .Distinct()
            .ToArray();
        Assert.Equal(new[] { "EmailOutbox" }, stems);
    }

    /// <summary>
    /// The <c>src/</c> filter is deliberate and matches <see cref="AlRunner.AppLoader.ExtractAl"/>.
    ///
    /// <para>It was widened to "every `.al` in the package" during development and that made
    /// things worse, which is why this asserts the narrow behaviour rather than the broad one:
    /// a package's other directories hold entitlements and permission sets referencing objects
    /// from apps that are NOT being compiled, and including them turned System Application's
    /// emit from 338 captured documents into AL1024 and zero.</para>
    ///
    /// <para>It also costs nothing on the apps this serves — measured on BC 28.1.49838,
    /// Business Foundation keeps 96/96 and System Application 1,319/1,319 of their `.al` under
    /// <c>src/</c>.</para>
    /// </summary>
    [Fact]
    public void OnlySrc_IsReturned_MatchingExtractAl()
    {
        var app = WritePackage("outside-src",
            ("NavxManifest.xml", "<Package><App Id=\"" + Guid.NewGuid() + "\" Name=\"N\" Publisher=\"P\" Version=\"1.0.0.0\"/></Package>"),
            ("src/Thing.Table.al", "table 1 Thing {}"),
            ("entitlements/Basic.Entitlement.al", "entitlement Basic {}"));

        var got = AlRunner.AppLoader.ExtractAlWithPaths(app);
        Assert.Single(got);
        Assert.Equal("src/Thing.Table.al", got[0].Path);

        // The same set as ExtractAl, differing only in how each entry is named.
        var flat = AlRunner.AppLoader.ExtractAl(app);
        Assert.Single(flat);
        Assert.Equal("Thing.Table.al", flat[0].Name);
    }

    /// <summary>
    /// The R2R shape Microsoft ships — outer archive, nested <c>.app</c> — resolves to the
    /// nested package's source, mirroring <see cref="AlRunner.AppLoader.ExtractAl"/>. Business
    /// Foundation and System Application are both this shape, so a reader that handled only the
    /// flat one would find no source in either.
    /// </summary>
    [Fact]
    public void ReadsThroughAnR2rNestedPackage()
    {
        var inner = Navx(ZipWith(
            ("NavxManifest.xml", "<Package><App Id=\"" + Guid.NewGuid() + "\" Name=\"N\" Publisher=\"P\" Version=\"1.0.0.0\"/></Package>"),
            ("src/Deep/Nested.Table.al", "table 7 Nested {}")));

        var dir = TestScratch.FlatDir("extract-al-with-paths-nested-");
        Directory.CreateDirectory(dir);
        var app = Path.Combine(dir, "Outer.app");
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var e = zip.CreateEntry("Inner.app");
                using var s = e.Open();
                s.Write(inner);
            }
            File.WriteAllBytes(app, Navx(ms.ToArray()));
        }

        var got = AlRunner.AppLoader.ExtractAlWithPaths(app);
        Assert.Single(got);
        Assert.Equal("src/Deep/Nested.Table.al", got[0].Path);
        Assert.Equal("table 7 Nested {}", got[0].Source);
    }

    /// <summary>
    /// A package with no AL answers with an empty list rather than throwing — that is the
    /// "nothing to produce" half of the availability-vs-failure split #3549 turns on, and it
    /// must be reachable without an exception for a symbol-only package to stay a quiet no-op.
    /// </summary>
    [Fact]
    public void SymbolOnlyPackage_IsEmptyNotAThrow()
    {
        var app = WritePackage("symbol-only",
            ("NavxManifest.xml", "<Package><App Id=\"" + Guid.NewGuid() + "\" Name=\"N\" Publisher=\"P\" Version=\"1.0.0.0\"/></Package>"),
            ("SymbolReference.json", "{}"));

        Assert.Empty(AlRunner.AppLoader.ExtractAlWithPaths(app));
    }
}
