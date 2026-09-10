// CodeunitSymbolSingleInstanceSpellingTests — a codeunit's SingleInstance is read from the
// spelling Microsoft's own .app files actually use.
//
// THE DEFECT THIS PINS (#3790, found by the CodeUnit metadata-equivalence comparison, #3782)
//   BcAppSymbolCache read the codeunit-only SingleInstance property with a hand-rolled
//   `string.Equals(value, "true")`, while every OTHER boolean in that file goes through
//   SymbolBool, which accepts BOTH spellings and whose own doc comment records that the symbol
//   file writes "1"/"0" in practice and "true"/"false" only as a tolerated form.
//
//   Microsoft writes "1". Measured on System Application 28.1.49838.53910: of its 533
//   codeunits, 38 state SingleInstance = "1", 5 state "0", 490 state nothing — and NOT ONE
//   states "true". So the comparison never matched, and CodeUnit Metadata (2000000137) answered
//   SingleInstance = false for all 38, which is also the truthful answer for a codeunit that
//   declares none. AL branching on the column cannot tell the two apart.
//
//   It survived because the only existing test for this path wrote "true" into its fixture —
//   the one spelling a real package never uses. Both spellings are asserted below.
//
// WHY A RUNNER-SIDE MECHANISM TEST
//   The BC-behaviour claim (what a real tier answers for a SingleInstance codeunit) is already
//   upstream and green: CodeunitMetadata_SingleInstanceCodeunit_ReportsTrueAndNoTableNo, cited
//   in AlRunner.Tests/CodeunitMetadataVirtualTableTests.cs. What no AL test can reach is the
//   PRECOMPILED-dependency route: an AL bundle's own codeunits are source-parsed, so the
//   symbol-file spelling never comes up. This file drives that route directly.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class CodeunitSymbolSingleInstanceSpellingTests : IDisposable
{
    private const int SingleInstanceNumeric = 61041;
    private const int SingleInstanceWord = 61042;
    private const int NotSingleInstance = 61043;
    private const int DeclaresNothing = 61044;

    private readonly string _root;

    public CodeunitSymbolSingleInstanceSpellingTests()
    {
        _root = TestScratch.Dir("al-runner-codeunit-singleinstance-spelling");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// The four shapes a real symbol file presents. <c>"1"</c> is what Microsoft's own packages
    /// write — 38 of System Application 28.1's 533 codeunits — and it is the one that used to
    /// read as false.
    /// </summary>
    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "1f7b3c8e-5d21-4a90-9b44-6e0f2a71c355",
          "Name": "SingleInstance Spelling Fixture",
          "Codeunits": [
            {
              "Id": {{SingleInstanceNumeric}},
              "Name": "Si Numeric",
              "Properties": [ { "Name": "SingleInstance", "Value": "1" } ]
            },
            {
              "Id": {{SingleInstanceWord}},
              "Name": "Si Word",
              "Properties": [ { "Name": "SingleInstance", "Value": "true" } ]
            },
            {
              "Id": {{NotSingleInstance}},
              "Name": "Si Numeric False",
              "Properties": [ { "Name": "SingleInstance", "Value": "0" } ]
            },
            {
              "Id": {{DeclaresNothing}},
              "Name": "Si Unstated",
              "Properties": []
            }
          ]
        }
        """;

    private void Register()
    {
        var appPath = Path.Combine(_root, "singleinstance-spelling.app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(SymbolReference);
        }
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
    }

    /// <summary>
    /// Reads the value back off the projection the metadata-equivalence harness compares, which
    /// is the same <c>EnumerateKnownCodeunitMetadata</c> row CodeUnit Metadata answers from —
    /// so this asserts the column's own source, not a second derivation written for the test.
    /// </summary>
    private static string SingleInstanceOf(int codeunitId)
    {
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(codeunitId);
        Assert.True(xml is not null, $"the runner derived no metadata for codeunit {codeunitId}");
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml!);
        return doc.DocumentElement!.GetAttribute("SingleInstance");
    }

    [Fact]
    public void NumericOne_TheSpellingMicrosoftsOwnPackagesUse_ReadsAsSingleInstance()
    {
        Register();

        // The defect: this answered "0" while the symbol file said "1".
        Assert.Equal("1", SingleInstanceOf(SingleInstanceNumeric));
    }

    [Fact]
    public void WordTrue_TheToleratedSpelling_StillReadsAsSingleInstance()
    {
        Register();

        Assert.Equal("1", SingleInstanceOf(SingleInstanceWord));
    }

    /// <summary>
    /// The negative half, and it is the one that stops the fix being "answer true always":
    /// a codeunit stating "0" and one stating nothing must both stay false.
    /// </summary>
    [Fact]
    public void NumericZero_AndAnUnstatedProperty_BothStayNotSingleInstance()
    {
        Register();

        Assert.Equal("0", SingleInstanceOf(NotSingleInstance));
        Assert.Equal("0", SingleInstanceOf(DeclaresNothing));
    }
}
