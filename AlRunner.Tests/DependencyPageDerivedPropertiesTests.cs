// DependencyPageDerivedPropertiesTests — issue #4282: three page properties whose rule is NOT
// "write it if the symbol file states it", which is the rule every scalar #3784 added follows.
//
// THE DISTINCTION THIS FILE EXISTS FOR
//   #4282 describes thirty allowlist entries as values "present, typed and unambiguous in the
//   symbol file the runner already parses". For these three that is half the question. What the
//   symbol file states and what BC's emitter WRITES are different counts, and the second is the
//   specification:
//
//     property             states   BC writes   rule
//     HelpLink                  6         235   derived from two AL properties plus a default
//     CaptionML (root attr)   196         196   write-iff-stated, "ENU=" + text
//     DataCaptionExpr          32          32   write-iff-stated, but a CONSTANT, not the value
//
//   Emitting HelpLink for the 6 pages that state it — the rule its neighbour UsageCategory
//   follows — is wrong for 229 of 235. This is the same trap #4267 hit from the other side,
//   where 107 pages state a Methods array and BC writes the element on 5.
//
// WHY A RUNNER-SIDE MECHANISM TEST AND NOT A CORPUS TEST
//   Unchanged from DependencyPagePropertiesFromSymbolTests: EmitPageXml runs only for a page the
//   runner never source-compiles, and a corpus test compiles its pages from source, taking the
//   real compiler's metadata path and never reaching this synthesizer
//   (bc-behavior-tests-go-upstream.md's structural case).
//
// WHAT THE EXPECTED VALUES REST ON
//   ONE BC build: 28.1.49838.53910, Business Foundation (11 pages) + System Application (224),
//   235 PageDefinition documents from tools/gen-metadata-ground-truth.sh cross-tabulated against
//   the same apps' shipped SymbolReference.json. Not a claim about the eight versions in
//   .github/bc-versions.txt. The per-property tables, the arms of the HelpLink partition and
//   what was deliberately left unimplemented are in docs/dependency-page-properties.md.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same reason as DependencyPagePropertiesFromSymbolTests: BcAppSymbolCache.Get() resolves
// through the process-global CacheRoots override, and the per-id metadata-xml memo is
// process-global too.
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyPageDerivedPropertiesTests
{
    private static string WriteApp(string dir, string symbolReferenceJson)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    // Distinctive ids in their own block: the dependency-page state is process-global, so an id
    // another test also declares would read back that test's cached document instead of this
    // one's.
    private const int StatedHelpLinkPageId = 88782001;
    private const int ContextHelpPageId = 88782002;
    private const int NoHelpPageId = 88782003;
    private const int BothHelpPageId = 88782004;
    private const int NoCaptionPageId = 88782005;

    private const string HelpLinkBase = "https://learn.microsoft.com/dynamics365/business-central/";

    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Pages": [
            {
              "Id": 88782001,
              "Name": "P4282 Stated HelpLink",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "700" },
                { "Name": "Caption", "Value": "Stated Help Link" },
                { "Name": "HelpLink", "Value": "https://go.microsoft.com/fwlink/?linkid=2149387" },
                { "Name": "DataCaptionExpression", "Value": "Rec.\"Related Table Caption\"" }
              ]
            },
            {
              "Id": 88782002,
              "Name": "P4282 Context Help",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "700" },
                { "Name": "Caption", "Value": "Context Help" },
                { "Name": "ContextSensitiveHelpPage", "Value": "ui-enter-date-ranges" }
              ]
            },
            {
              "Id": 88782003,
              "Name": "P4282 No Help",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "700" },
                { "Name": "Caption", "Value": "No Help At All" }
              ]
            },
            {
              "Id": 88782004,
              "Name": "P4282 Both Help",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "700" },
                { "Name": "Caption", "Value": "Both Help" },
                { "Name": "HelpLink", "Value": "https://go.microsoft.com/fwlink/?linkid=2221526" },
                { "Name": "ContextSensitiveHelpPage", "Value": "across-videos" }
              ]
            },
            {
              "Id": 88782005,
              "Name": "P4282 No Caption",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "700" }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// THE HEADLINE. <c>HelpLink</c> is written on every page, and the value depends on which of
    /// two AL properties the page states — so all three arms are asserted, each with a DISTINCT
    /// expected string.
    ///
    /// <para>The three-arm shape is the point: a fix that wrote the base URL unconditionally
    /// satisfies the third arm and fails the other two, and the write-iff-stated rule this
    /// replaced satisfies the first and fails the other two. No single constant passes.</para>
    ///
    /// <para>Measured on BC 28.1.49838.53910 over 235 pages: 6 state <c>HelpLink</c>, 36 state
    /// <c>ContextSensitiveHelpPage</c>, 193 state neither, and BC's value matched the arm's rule
    /// on every page in every arm. 6 + 36 + 193 = 235, so the partition is total — there is no
    /// fourth case on that build.</para>
    /// </summary>
    [Fact]
    public void HelpLink_IsDerived_AndWrittenForEveryPage()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-derived-4282");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            // Arm 1 — a stated HelpLink goes through verbatim, NOT joined to the base.
            Assert.Equal(
                "https://go.microsoft.com/fwlink/?linkid=2149387",
                ReadProperties(StatedHelpLinkPageId).GetAttribute("HelpLink"));

            // Arm 2 — ContextSensitiveHelpPage is RELATIVE and is joined to the base with no
            // separator, which is the form BC's own 36 values take.
            Assert.Equal(
                HelpLinkBase + "ui-enter-date-ranges",
                ReadProperties(ContextHelpPageId).GetAttribute("HelpLink"));

            // Arm 3 — the 193-page case, and the one the old code got wrong by writing nothing.
            // An absent attribute and this string are different documents to BC's reader.
            Assert.Equal(HelpLinkBase, ReadProperties(NoHelpPageId).GetAttribute("HelpLink"));
            Assert.True(ReadProperties(NoHelpPageId).HasAttribute("HelpLink"));

            // Precedence, asserted rather than assumed: a page stating BOTH takes the explicit
            // HelpLink. No Microsoft page in the 235 states both, so this arm is the runner's
            // own declared rule for an ISV page that does, not a measurement of BC -- it is
            // pinned here so a later editor changes it deliberately.
            Assert.Equal(
                "https://go.microsoft.com/fwlink/?linkid=2221526",
                ReadProperties(BothHelpPageId).GetAttribute("HelpLink"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The page's caption, as the ROOT attribute BC writes it on and
    /// <c>PageDefinition(XmlNode)</c> reads <c>CaptionMLString</c> from — not the
    /// <c>&lt;CaptionML&gt;</c> child element inside <c>&lt;Properties&gt;</c>, which the parser
    /// ignores and which left that member empty for all 196 pages carrying a caption.
    ///
    /// <para>Both directions: a page stating no <c>Caption</c> gets no attribute, because 39 of
    /// the 235 state none and BC writes none for them.</para>
    /// </summary>
    [Fact]
    public void CaptionML_IsARootAttribute_InBcsMultiLanguageForm()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-derived-4282");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            Assert.Equal("ENU=Stated Help Link", ReadRoot(StatedHelpLinkPageId).GetAttribute("CaptionML"));
            Assert.Equal("ENU=Context Help", ReadRoot(ContextHelpPageId).GetAttribute("CaptionML"));

            // The negative half. BC writes CaptionML on exactly the 196 pages stating Caption,
            // so inventing one here would be a different document rather than a missing value.
            Assert.False(ReadRoot(NoCaptionPageId).HasAttribute("CaptionML"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// <c>DataCaptionExpr</c> records THAT a page has a caption expression, never what it is:
    /// BC writes the fixed marker <c>DataCaptionExprCode</c> on all 32 pages stating
    /// <c>DataCaptionExpression</c>, and the AL text compiles into the page's own IL.
    ///
    /// <para>The assertion is deliberately inequality against the stated text as well as
    /// equality against the marker, because writing the expression through is the plausible
    /// wrong fix here — it is what "carry what the symbol file states" produces.</para>
    /// </summary>
    [Fact]
    public void DataCaptionExpr_IsBcsMarker_NotTheAlExpression()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-derived-4282");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            var stated = ReadProperties(StatedHelpLinkPageId);
            Assert.Equal("DataCaptionExprCode", stated.GetAttribute("DataCaptionExpr"));
            Assert.NotEqual("Rec.\"Related Table Caption\"", stated.GetAttribute("DataCaptionExpr"));

            // The negative half: 203 of the 235 state no expression and BC writes no attribute.
            Assert.False(ReadProperties(NoHelpPageId).HasAttribute("DataCaptionExpr"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static XmlElement ReadRoot(int pageId)
    {
        var xml = RecordPatches.TryBuildDependencyPageMetadata(pageId);
        Assert.NotNull(xml);

        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc.DocumentElement!;
    }

    private static XmlElement ReadProperties(int pageId)
    {
        var doc = ReadRoot(pageId).OwnerDocument!;
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        return (XmlElement)doc.DocumentElement!.SelectSingleNode("m:Properties", ns)!;
    }
}
