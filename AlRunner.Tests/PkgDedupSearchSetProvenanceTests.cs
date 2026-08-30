// PkgDedupSearchSetProvenanceTests — #2178, the honesty half.
//
// When BcCompiler.DeduplicateAppPackageDirs collapses the scan set, every dir the caller
// gave it is replaced by ONE content-addressed staging directory under
// <temp>/al-runner-pkgdedup/<hash>. BC's own AL1022 text then reads:
//
//   AL1022 A package with publisher 'X', name 'Y', and a version compatible with '1.0.0.0'
//   could not be found in the package cache folders: /tmp/al-runner-pkgdedup/1c98b3f208e9c69a
//
// which is accurate and useless: the reader cannot tell whether their --package-cache
// directory reached the compile at all, and cannot see that a package matching exactly
// that identity WAS in one of their directories and was deliberately left out of the
// staging copy for carrying no SymbolReference.json.
//
// #2108 is the precedent for why this matters: a search-set message that does not describe
// the set actually searched sends people to look in the wrong place.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class PkgDedupSearchSetProvenanceTests : IDisposable
{
    private readonly string _root;

    public PkgDedupSearchSetProvenanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-pkgdedup-provenance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Positive: a staging directory's description names every original package dir it was
    /// built from, and names the package that was dropped for carrying no
    /// SymbolReference.json — with its publisher, name, version and path, so the reader can
    /// go and look at the file.
    ///
    /// Negative, in the same run: the symbol-BEARING package must NOT be reported as
    /// excluded (a description that listed everything would be no more informative than the
    /// bare staging path it replaces), and a diagnostic that names no staging directory at
    /// all must produce no description rather than an invented one.
    /// </summary>
    [Fact]
    public void StagedSearchSet_IsDescribedByItsOriginDirsAndDroppedPackages()
    {
        var withSymbols = Path.Combine(_root, "with-symbols");
        var handedIn = Path.Combine(_root, "handed-in");
        Directory.CreateDirectory(withSymbols);
        Directory.CreateDirectory(handedIn);

        // A real, compiler-usable package …
        WriteApp(withSymbols, "Alpha.app",
            "11111111-0000-0000-0000-0000000000a1", "Prov Alpha", "Contoso", "1.0.0.0",
            withSymbolReference: true);
        // … and the shape from the issue: a source-only package the user copied into a dir
        // they passed on --package-cache. It cannot serve symbols, so dedup drops it — and
        // dropping ANY package is what forces the staging branch to engage at all.
        WriteApp(handedIn, "Beta.app",
            "22222222-0000-0000-0000-0000000000b2", "Prov Beta", "Contoso", "2.3.4.5",
            withSymbolReference: false);

        var packageDirs = new List<string> { withSymbols, handedIn };
        var staged = InvokeDeduplicateAppPackageDirs(packageDirs, excludeAppId: null);

        // Precondition: the staging branch really engaged, else the rest is vacuous.
        Assert.Single(staged);
        Assert.NotEqual(packageDirs, staged);

        var diagnostic =
            "AL1022 A package with publisher 'Contoso', name 'Prov Beta', and a version " +
            $"compatible with '2.3.4.5' could not be found in the package cache folders: {staged[0]}";

        var description = BcCompiler.DescribeStagedSearchSet(diagnostic);

        Assert.NotNull(description);
        // Every dir the caller handed in is named, including the one whose only package was
        // dropped — that dir being invisible is exactly what made #2178 read as "my
        // --package-cache never reached the compile".
        Assert.Contains(withSymbols, description);
        Assert.Contains(handedIn, description);
        // The dropped package is named in full, with the reason.
        Assert.Contains("Contoso/Prov Beta 2.3.4.5", description);
        Assert.Contains(Path.Combine(handedIn, "Beta.app"), description);
        Assert.Contains("no SymbolReference.json", description);
        // …and the package that WAS staged is not reported as excluded.
        Assert.DoesNotContain("Prov Alpha", description);
    }

    /// <summary>
    /// Negative: a diagnostic naming no staging directory gets no description. The helper
    /// must stay silent rather than attach the provenance of some unrelated staging dir
    /// that happens to be in the process-wide table.
    /// </summary>
    [Fact]
    public void DiagnosticWithoutAStagingDir_GetsNoDescription()
    {
        var withSymbols = Path.Combine(_root, "with-symbols");
        var handedIn = Path.Combine(_root, "handed-in");
        Directory.CreateDirectory(withSymbols);
        Directory.CreateDirectory(handedIn);
        WriteApp(withSymbols, "Alpha.app",
            "33333333-0000-0000-0000-0000000000c3", "Prov Gamma", "Contoso", "1.0.0.0",
            withSymbolReference: true);
        WriteApp(handedIn, "Beta.app",
            "44444444-0000-0000-0000-0000000000d4", "Prov Delta", "Contoso", "2.0.0.0",
            withSymbolReference: false);

        // Populate the provenance table, so "returns null" cannot be explained by it
        // being empty.
        var staged = InvokeDeduplicateAppPackageDirs(
            new List<string> { withSymbols, handedIn }, excludeAppId: null);
        Assert.Single(staged);

        Assert.Null(BcCompiler.DescribeStagedSearchSet(
            "AL0185 Codeunit 'Something' is missing"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static List<string> InvokeDeduplicateAppPackageDirs(List<string> packageDirs, Guid? excludeAppId)
    {
        var method = typeof(BcCompiler)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "DeduplicateAppPackageDirs" && m.GetParameters().Length == 2)
            ?? throw new InvalidOperationException(
                "BcCompiler.DeduplicateAppPackageDirs(dirs, excludeAppId) not found by reflection — signature may have changed.");
        return (List<string>)method.Invoke(null, new object?[] { packageDirs, excludeAppId })!;
    }

    private static void WriteApp(string dir, string fileName, string appId, string name,
        string publisher, string version, bool withSymbolReference)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("NavxManifest.xml");
            using (var es = entry.Open()) es.Write(Encoding.UTF8.GetBytes(xml));
            if (withSymbolReference)
            {
                var symEntry = zip.CreateEntry("SymbolReference.json");
                using var symStream = symEntry.Open();
                symStream.Write(Encoding.UTF8.GetBytes("{}"));
            }
        }
        var zipBytes = ms.ToArray();
        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        File.WriteAllBytes(Path.Combine(dir, fileName), result);
    }
}
