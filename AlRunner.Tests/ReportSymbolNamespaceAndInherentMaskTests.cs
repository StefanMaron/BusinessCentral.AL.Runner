// ReportSymbolNamespaceAndInherentMaskTests — the three report-level properties
// SymbolReference.json states that the Report derivation was dropping (#3808).
//
// THE GAP
//   BcAppSymbolCache's ReportSymbol carried neither the report's Namespaces tree path nor
//   either inherent-permission mask, so DependencyReportMetadata.EmitReportXml wrote no
//   <ALNamespace>, <InherentEntitlements> or <InherentPermissions> element. BC's own
//   MetaReport(XmlElement, …) reader therefore left its constructor defaults behind — null
//   for the namespace and PermissionMask.None for both masks — for every report living in a
//   precompiled dependency.
//
// WHAT BC'S READER ACTUALLY TAKES, which is what these assertions are written against
//   Read off MetaReport..ctor in Microsoft.Dynamics.Nav.Types.dll 28.1.49838.53910:
//     ALNAMESPACE          -> ALNamespace = val.InnerText                       (verbatim)
//     INHERENTENTITLEMENTS -> Enum.Parse(typeof(PermissionMask), val.InnerText)
//     INHERENTPERMISSIONS  -> Enum.Parse(typeof(PermissionMask), val.InnerText)
//   So the masks go out as the DECODED NUMBER, not the AL letter spelling: "X" is not a
//   PermissionMask member and Enum.Parse would throw on it.
//
// THE POPULATION, stated honestly because the two halves differ
//   Measured over every .app in ~/.al-runner/platform-apps at 28.1.49838.53910, walking the
//   Namespaces tree (the flat top-level Reports array is empty in both apps that ship any):
//     * ALNamespace     — 660 of 660 reports sit under a namespace (Base Application 659,
//       System Application 1). NOT a population of one.
//     * the two masks   — 1 of 660 states either, and it spells both "X" (report 9810).
//   The issue's "n = 1" is the metadata-equivalence harness's loaded bundle, which excludes
//   Base Application on cost; it is not the population of the symbol files themselves.
//
//   Because no shipped report states a lowercase or mixed-case mask, the decoder's indirect
//   bits are unexercised by the real population — so the fixture below states them anyway
//   (see LowercaseMask/MixedCaseMask), which is the point of reusing the shared decoder
//   rather than writing a second one that happens to be right on "X".

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.Get() resolves its on-disk path through the process-global CacheRoots
// override, so this joins CacheRootsSerialCollection like its sibling report tests.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class ReportSymbolNamespaceAndInherentMaskTests
{
    // Report 9810 "Change Password" as System Application 28.1.49838.53910 actually states it:
    // nested two deep, ProcessingOnly = 1, UseRequestPage = 0, both masks "X".
    private const int ChangePassword = 9810;
    // Declares nothing at all — the negative direction for all three properties.
    private const int DeclaresNothing = 9811;
    // The decoder arms no shipped report reaches. "x" is indirect Execute (bit 4+5 = 512) and
    // "rX" is indirect Read (32) | direct Execute (16) = 48 — a case-collapsing parse answers
    // 16 and 17 for these, which is the defect #3933 tracks on the page side.
    private const int LowercaseMask = 9812;
    private const int MixedCaseMask = 9813;
    // At the ROOT of the symbol file rather than in the Namespaces tree: a report that is
    // genuinely outside every namespace must answer no namespace, not an invented one.
    private const int RootLevel = 9814;

    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "3f1a77c6-2a4e-4a1f-9d6c-51e0a7b4c8d2",
          "Name": "Report Namespace Fixture",
          "Reports": [
            { "Id": {{RootLevel}}, "Name": "Root Level", "DataItems": [] }
          ],
          "Namespaces": [
            {
              "Name": "System",
              "Namespaces": [
                {
                  "Name": "Security",
                  "Namespaces": [
                    {
                      "Name": "AccessControl",
                      "Reports": [
                        {
                          "Id": {{ChangePassword}},
                          "Name": "Change Password",
                          "DataItems": [],
                          "Properties": [
                            { "Name": "ProcessingOnly", "Value": "1" },
                            { "Name": "UseRequestPage", "Value": "0" },
                            { "Name": "InherentEntitlements", "Value": "X" },
                            { "Name": "InherentPermissions", "Value": "X" }
                          ]
                        },
                        { "Id": {{DeclaresNothing}}, "Name": "Declares Nothing", "DataItems": [] },
                        {
                          "Id": {{LowercaseMask}},
                          "Name": "Lowercase Mask",
                          "DataItems": [],
                          "Properties": [
                            { "Name": "InherentEntitlements", "Value": "x" }
                          ]
                        },
                        {
                          "Id": {{MixedCaseMask}},
                          "Name": "Mixed Case Mask",
                          "DataItems": [],
                          "Properties": [
                            { "Name": "InherentPermissions", "Value": "rX" }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static string WriteApp(string dir)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(SymbolReference);
        return appPath;
    }

    private static XmlElement Emit(BcAppSymbolCache.ReportSymbol report)
    {
        var doc = new XmlDocument();
        doc.LoadXml(RecordPatches.EmitReportXml(report, sourceExprByColumn: null));
        return doc.DocumentElement!;
    }

    private static string? Element(XmlElement root, string name)
        => root.SelectSingleNode(name) is XmlNode n ? n.InnerText : null;

    /// <summary>
    /// The symbol side: ReportSymbol carries the tree path and both mask letter strings. The
    /// masks stay the LETTERS here — decoding them at parse time would throw away the case
    /// distinction the consumer needs.
    /// </summary>
    [Fact]
    public void TheSymbolCarriesTheNamespacePathAndBothMaskLettersVerbatim()
    {
        var dir = TestScratch.Dir("al-runner-report-namespace-mask");
        Directory.CreateDirectory(dir);
        try
        {
            var reports = BcAppSymbolCache.Get(WriteApp(dir)).Reports;

            var changePassword = Assert.Single(reports, r => r.Id == ChangePassword);
            Assert.Equal("System.Security.AccessControl", changePassword.ALNamespace);
            Assert.Equal("X", changePassword.InherentEntitlements);
            Assert.Equal("X", changePassword.InherentPermissions);

            // Negative: a report stating no mask must carry null, never an empty string that
            // would decode to a mask of 0 and read as a declared "no permissions".
            var nothing = Assert.Single(reports, r => r.Id == DeclaresNothing);
            Assert.Equal("System.Security.AccessControl", nothing.ALNamespace);
            Assert.Null(nothing.InherentEntitlements);
            Assert.Null(nothing.InherentPermissions);

            // Case is preserved, which is the whole reason the letters are carried rather than
            // a decoded int computed at parse time.
            Assert.Equal("x", Assert.Single(reports, r => r.Id == LowercaseMask).InherentEntitlements);
            Assert.Equal("rX", Assert.Single(reports, r => r.Id == MixedCaseMask).InherentPermissions);

            // Negative: a report outside every namespace states none.
            Assert.Null(Assert.Single(reports, r => r.Id == RootLevel).ALNamespace);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The document BC's reader actually consumes. <c>&lt;ALNamespace&gt;</c> is taken verbatim
    /// and both masks are parsed as <c>PermissionMask</c>, so the numbers below are what BC
    /// resolves to <c>Execute</c> — the value the issue measured BC answering while the runner
    /// answered <c>None</c>.
    /// </summary>
    [Fact]
    public void TheEmittedDocumentStatesTheNamespaceAndTheDecodedMasks()
    {
        var dir = TestScratch.Dir("al-runner-report-namespace-mask-xml");
        Directory.CreateDirectory(dir);
        try
        {
            var reports = BcAppSymbolCache.Get(WriteApp(dir)).Reports;

            var changePassword = Emit(Assert.Single(reports, r => r.Id == ChangePassword));
            Assert.Equal("System.Security.AccessControl", Element(changePassword, "ALNamespace"));
            // "X" -> bit 4 -> 16, which PermissionMask names Execute.
            Assert.Equal("16", Element(changePassword, "InherentEntitlements"));
            Assert.Equal("16", Element(changePassword, "InherentPermissions"));
            // The properties that already agreed must keep agreeing — this document is the
            // whole runtime metadata for the report, not just the three new elements.
            Assert.Equal("1", Element(changePassword, "ProcessingOnly"));
            Assert.Equal("0", Element(changePassword, "UseRequestPage"));

            // Negative: an unstated mask writes NO element, so BC's reader keeps its own
            // default. Writing <InherentEntitlements>0</InherentEntitlements> would be a claim
            // the symbol file never made.
            var nothing = Emit(Assert.Single(reports, r => r.Id == DeclaresNothing));
            Assert.Null(Element(nothing, "InherentEntitlements"));
            Assert.Null(Element(nothing, "InherentPermissions"));
            Assert.Equal("System.Security.AccessControl", Element(nothing, "ALNamespace"));

            // The decoder arms no shipped report reaches, asserted so a case-collapsing
            // reimplementation fails here rather than shipping: "x" is 512, not 16, and "rX"
            // is 48, not 17.
            Assert.Equal("512", Element(Emit(Assert.Single(reports, r => r.Id == LowercaseMask)),
                "InherentEntitlements"));
            Assert.Equal("48", Element(Emit(Assert.Single(reports, r => r.Id == MixedCaseMask)),
                "InherentPermissions"));

            // Negative: no namespace stated means no element, not an empty one.
            Assert.Null(Element(Emit(Assert.Single(reports, r => r.Id == RootLevel)), "ALNamespace"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A mask letter the table does not name raises the shared decoder's shape gap rather than
    /// contributing no bit — a mask short one letter is indistinguishable from a narrower
    /// permission the report really declared (loud-failures.md). Pinned here because this is a
    /// second entry point into that decoder.
    /// </summary>
    [Fact]
    public void AnUnreadableMaskLetterRefusesRatherThanDroppingTheCharacter()
    {
        var dir = TestScratch.Dir("al-runner-report-mask-gap");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = Path.Combine(dir, "gap.app");
            using (var zip = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
            {
                var entry = za.CreateEntry("SymbolReference.json");
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write($$"""
                    {
                      "RuntimeVersion": "15.1",
                      "Namespaces": [
                        {
                          "Name": "System",
                          "Reports": [
                            {
                              "Id": {{ChangePassword}},
                              "Name": "Bad Mask",
                              "DataItems": [],
                              "Properties": [
                                { "Name": "InherentPermissions", "Value": "Q" }
                              ]
                            }
                          ]
                        }
                      ]
                    }
                    """);
            }

            var report = Assert.Single(BcAppSymbolCache.Get(appPath).Reports);
            var ex = Assert.ThrowsAny<Exception>(() => RecordPatches.EmitReportXml(report, null));
            Assert.Contains("'Q'", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
