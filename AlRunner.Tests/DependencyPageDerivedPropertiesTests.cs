// DependencyPageDerivedPropertiesTests — issue #4282: page properties whose rule is NOT
// "write it if the symbol file states it", which is the rule every scalar #3784 added follows.
// The first pass (HelpLink, CaptionML, DataCaptionExpr) is described below; the second
// (AnalysisModeEnabled, CardFormID, IndirectPermissions, the no-PageType IsPreview) is in
// docs/dependency-page-properties.md, measured on four bundles and a compiled probe.
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

    // ── The three derivations of the second pass: AnalysisModeEnabled, CardFormID and
    //    IndirectPermissions. Rules and measurements: docs/dependency-page-properties.md.

    private const int NoPageTypePageId = 88782010;
    private const int ListPageId = 88782011;
    private const int WorksheetPageId = 88782012;
    private const int StatedCardPageId = 88782013;
    private const int ListPartPageId = 88782014;
    private const int ListStatedOffPageId = 88782015;
    private const int NumericCardListPageId = 88782016;
    private const int UnresolvedCardListPageId = 88782017;
    private const int PermissionsPageId = 88782018;
    private const int PermissionsNoSourcePageId = 88782019;
    private const int UnreadablePermissionsPageId = 88782020;
    private const int PermTableId = 88782050;

    private const string DerivationSymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Tables": [
            { "Id": 88782050, "Name": "P4282 Perm Table",
              "Fields": [ { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code" } } ] }
          ],
          "Pages": [
            { "Id": 88782010, "Name": "P4282 No PageType",
              "Properties": [ { "Name": "SourceTable", "Value": "700" } ] },
            { "Id": 88782011, "Name": "P4282 List",
              "Properties": [ { "Name": "PageType", "Value": "List" }, { "Name": "SourceTable", "Value": "700" },
                              { "Name": "CardPageId", "Value": "P4282 Stated Card" } ] },
            { "Id": 88782012, "Name": "P4282 Worksheet",
              "Properties": [ { "Name": "PageType", "Value": "Worksheet" }, { "Name": "SourceTable", "Value": "700" } ] },
            { "Id": 88782013, "Name": "P4282 Stated Card",
              "Properties": [ { "Name": "PageType", "Value": "Card" }, { "Name": "SourceTable", "Value": "700" } ] },
            { "Id": 88782014, "Name": "P4282 ListPart",
              "Properties": [ { "Name": "PageType", "Value": "ListPart" }, { "Name": "SourceTable", "Value": "700" } ] },
            { "Id": 88782015, "Name": "P4282 List Stated Off",
              "Properties": [ { "Name": "PageType", "Value": "List" }, { "Name": "SourceTable", "Value": "700" },
                              { "Name": "AnalysisModeEnabled", "Value": "0" } ] },
            { "Id": 88782016, "Name": "P4282 Numeric Card List",
              "Properties": [ { "Name": "PageType", "Value": "List" }, { "Name": "SourceTable", "Value": "700" },
                              { "Name": "CardPageID", "Value": "88782013" } ] },
            { "Id": 88782017, "Name": "P4282 Unresolved Card List",
              "Properties": [ { "Name": "PageType", "Value": "List" }, { "Name": "SourceTable", "Value": "700" },
                              { "Name": "CardPageId", "Value": "P4282 No Such Card" } ] },
            { "Id": 88782018, "Name": "P4282 Permissions",
              "Properties": [ { "Name": "PageType", "Value": "Card" }, { "Name": "SourceTable", "Value": "700" },
                              { "Name": "Permissions", "Value": "tabledata \"P4282 Perm Table\" = rd,\n                  tabledata 700 = RIMD" } ] },
            { "Id": 88782019, "Name": "P4282 Permissions No Source",
              "Properties": [ { "Name": "PageType", "Value": "Card" },
                              { "Name": "Permissions", "Value": "tabledata 700 = r" } ] },
            { "Id": 88782020, "Name": "P4282 Unreadable Permissions",
              "Properties": [ { "Name": "PageType", "Value": "Card" }, { "Name": "SourceTable", "Value": "700" },
                              { "Name": "Permissions", "Value": "tabledata 700 = r, tabledata \"P4282 No Such Table\" = r" } ] }
          ]
        }
        """;

    /// <summary>
    /// <c>AnalysisModeEnabled</c> is DERIVED: <c>"1"</c> for List, Worksheet and a page stating
    /// no <c>PageType</c>; the stated value when stated; absent otherwise. Every arm has its own
    /// expected answer, so neither "write iff stated" nor "write 1 for List" passes.
    ///
    /// <para>The no-PageType arm is the one BC's own apps show only once (page 1998) and the
    /// runner's symbol folds into <c>"Card"</c>, so it is asserted against a stated Card that
    /// must stay attribute-free. It also carries <c>IsPreview="0"</c>, which a stated Card does
    /// not.</para>
    /// </summary>
    [Fact]
    public void AnalysisModeEnabled_IsDerivedFromPageType_AndAStatedValueWins()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-derived-4282");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, DerivationSymbolReference));

            Assert.Equal("1", ReadProperties(ListPageId).GetAttribute("AnalysisModeEnabled"));
            Assert.Equal("1", ReadProperties(WorksheetPageId).GetAttribute("AnalysisModeEnabled"));
            Assert.Equal("1", ReadProperties(NoPageTypePageId).GetAttribute("AnalysisModeEnabled"));
            Assert.Equal("0", ReadProperties(ListStatedOffPageId).GetAttribute("AnalysisModeEnabled"));

            Assert.False(ReadProperties(StatedCardPageId).HasAttribute("AnalysisModeEnabled"));
            Assert.False(ReadProperties(ListPartPageId).HasAttribute("AnalysisModeEnabled"));

            // The unstated PageType is still emitted as Card, as BC does.
            Assert.Equal("Card", ReadProperties(NoPageTypePageId).GetAttribute("PageType"));
            Assert.Equal("0", ReadProperties(NoPageTypePageId).GetAttribute("IsPreview"));
            Assert.False(ReadProperties(StatedCardPageId).HasAttribute("IsPreview"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// <c>CardFormID</c> carries the RESOLVED id of the page <c>CardPageId</c> names — both the
    /// name form every Microsoft page uses and the numeric form — and nothing when the name
    /// resolves to no page, or when none is stated.
    /// </summary>
    [Fact]
    public void CardFormId_IsTheResolvedIdOfTheNamedCardPage()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-derived-4282");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, DerivationSymbolReference));

            Assert.Equal(StatedCardPageId.ToString(), ReadProperties(ListPageId).GetAttribute("CardFormID"));
            Assert.Equal(StatedCardPageId.ToString(), ReadProperties(NumericCardListPageId).GetAttribute("CardFormID"));

            Assert.False(ReadProperties(UnresolvedCardListPageId).HasAttribute("CardFormID"));
            Assert.False(ReadProperties(WorksheetPageId).HasAttribute("CardFormID"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A page's AL <c>Permissions</c> becomes <c>SourceObject/@IndirectPermissions</c>: table ids
    /// and INDIRECT masks in declared order, terminated by <c>0, 0</c>. <c>rd</c> is 288 and
    /// <c>RIMD</c> is 480 — upper case gives the indirect bits too, as BC's compiler does.
    ///
    /// <para>Two negatives: no <c>SourceTable</c> means no attribute even with
    /// <c>Permissions</c> stated, and one unresolvable table withdraws the whole vector rather
    /// than writing the entries it could read.</para>
    /// </summary>
    [Fact]
    public void IndirectPermissions_IsTheResolvedVector_OnlyForASourceTablePage()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-derived-4282");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, DerivationSymbolReference));

            Assert.Equal(
                $"{PermTableId}, 288, 700, 480, 0, 0",
                ReadSourceObject(PermissionsPageId).GetAttribute("IndirectPermissions"));

            Assert.False(ReadSourceObject(PermissionsNoSourcePageId).HasAttribute("IndirectPermissions"));
            Assert.False(ReadSourceObject(UnreadablePermissionsPageId).HasAttribute("IndirectPermissions"));
            Assert.False(ReadSourceObject(StatedCardPageId).HasAttribute("IndirectPermissions"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The three new PageSymbol members sit behind BcAppSymbolCache's on-disk cache, so the same
    /// answers are asserted on a WARM read -- served from disk, not re-parsed -- as on the cold
    /// one (local-test-scope.md: a fix correct cold can be wrong warm).
    /// </summary>
    [Fact]
    public void TheDerivations_AnswerTheSameOnAWarmSymbolCacheRead()
    {
        var dir = TestScratch.Dir("al-runner-dep-page-derived-4282");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, DerivationSymbolReference);
            void AssertAll()
            {
                Assert.Equal("1", ReadProperties(NoPageTypePageId).GetAttribute("AnalysisModeEnabled"));
                Assert.Equal("0", ReadProperties(ListStatedOffPageId).GetAttribute("AnalysisModeEnabled"));
                Assert.Equal(StatedCardPageId.ToString(), ReadProperties(ListPageId).GetAttribute("CardFormID"));
                Assert.Equal(
                    $"{PermTableId}, 288, 700, 480, 0, 0",
                    ReadSourceObject(PermissionsPageId).GetAttribute("IndirectPermissions"));
            }

            BcAppSymbolCache.ResetProcessCacheForTests();
            RecordPatches.ResetForReload();
            RecordPatches.AddBcAppPath(appPath);
            AssertAll();
            var parsesCold = BcAppSymbolCache.ParseInvocationCountForTests(appPath);
            Assert.True(parsesCold >= 1, "the cold read never parsed the symbol file");

            BcAppSymbolCache.ResetProcessCacheForTests();
            RecordPatches.ResetForReload();
            RecordPatches.AddBcAppPath(appPath);
            AssertAll();
            Assert.Equal(parsesCold, BcAppSymbolCache.ParseInvocationCountForTests(appPath));
        }
        finally
        {
            RecordPatches.ResetForReload();
            BcAppSymbolCache.ResetProcessCacheForTests();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("tabledata \"A, B\" = r", "10, 32, 0, 0")]
    [InlineData("tabledata Plain = imd", "11, 448, 0, 0")]
    [InlineData("TableData \"A, B\" = Rm,\n   tabledata 42 = D", "10, 160, 42, 256, 0, 0")]
    public void IndirectPermissionsVector_ParsesQuotedNamesCaseAndIds(string stated, string expected)
    {
        int Resolve(string name) => name switch { "A, B" => 10, "Plain" => 11, _ => -1 };
        Assert.Equal(expected, RecordPatches.TryBuildIndirectPermissionsVector(stated, Resolve, out var unreadable));
        Assert.Null(unreadable);
    }

    [Theory]
    [InlineData("tabledata Plain = rx")]
    [InlineData("codeunit Plain = X")]
    [InlineData("tabledata Missing = r")]
    public void IndirectPermissionsVector_RefusesWhatItCannotRead(string stated)
    {
        int Resolve(string name) => name == "Plain" ? 11 : -1;
        Assert.Null(RecordPatches.TryBuildIndirectPermissionsVector(stated, Resolve, out var unreadable));
        Assert.NotNull(unreadable);
    }

    private static XmlElement ReadSourceObject(int pageId)
    {
        var props = ReadProperties(pageId);
        return (XmlElement)props.GetElementsByTagName("SourceObject", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects")[0]!;
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
