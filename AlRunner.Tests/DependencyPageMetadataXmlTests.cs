// DependencyPageMetadataXmlTests — pins the runner's own C# mechanism for issue #1939:
// reconstructing a real PageDefinition metadata document for a page declared only by a
// PRECOMPILED dependency .app's SymbolReference.json (RecordPatches.TryBuildDependencyPageMetadata
// / HasDependencyPageMetadata, AlRunner/Patches/DependencyPageMetadataXml.cs).
//
// What this proves, and what it deliberately does NOT
// -----------------------------------------------------
// The actual BC-observable claim — that a [ModalPageHandler] now fires for
// `Page "Error Messages".RunModal()` instead of NavTestExecution.FindPageType NREing on a
// null MasterPage — is plain BC behaviour and belongs upstream against a real service tier
// (see .claude/rules/bc-behavior-tests-go-upstream.md); it is proved there, not here. This
// file pins the narrower, runner-only mechanism claim underneath it: given a dependency
// .app's SymbolReference.json, the synthesized XML actually carries the PageType and
// SourceObject a real page's metadata carries, and an unknown page id gets neither a
// document nor a false "yes" from the opt-in check — the same shape as
// BcAppSymbolCachePageMetadataTests one layer down, just proving the XML BUILDER rather
// than the SymbolReference.json PARSER.

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same reason as BcAppSymbolCachePageMetadataTests: BcAppSymbolCache.Get() resolves through
// the process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public class DependencyPageMetadataXmlTests
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

    // Distinctive, unlikely-to-collide ids: RecordPatches' dependency-page state
    // (_bcAppPaths, the per-id metadata-xml cache) is process-global, so reusing an id
    // another test/fixture might also declare (e.g. Base Application's real 700/456, or
    // BcAppSymbolCachePageMetadataTests' 21/22/23) would risk reading back another test's
    // cached answer instead of this one's.
    private const int ListPageId = 88123401;
    private const int UnknownPageId = 88123409;
    private const int NavigatePageId = 88123402;
    private const int SplitKeyPageId = 88123403;
    private const int PlainLinesPageId = 88123404;

    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Pages": [
            {
              "Id": 88123401,
              "Name": "DPX Test List Page",
              "Properties": [
                { "Name": "Caption", "Value": "DPX Test Caption" },
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "700" },
                { "Name": "SourceTableTemporary", "Value": "true" }
              ]
            },
            {
              "Id": 88123402,
              "Name": "DPX Test Navigate Page",
              "Properties": [
                { "Name": "Caption", "Value": "DPX Wizard Caption" },
                { "Name": "PageType", "Value": "NavigatePage" }
              ]
            },
            {
              "Id": 88123403,
              "Name": "DPX Test Split Key Lines",
              "Properties": [
                { "Name": "PageType", "Value": "ListPart" },
                { "Name": "SourceTable", "Value": "701" },
                { "Name": "AutoSplitKey", "Value": "1" },
                { "Name": "MultipleNewLines", "Value": "1" },
                { "Name": "DelayedInsert", "Value": "1" }
              ]
            },
            {
              "Id": 88123404,
              "Name": "DPX Test Plain Lines",
              "Properties": [
                { "Name": "PageType", "Value": "ListPart" },
                { "Name": "SourceTable", "Value": "701" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void TryBuildDependencyPageMetadata_KnownPage_ProducesPageDefinitionWithRealPageTypeAndSourceTable()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            Assert.True(RecordPatches.HasDependencyPageMetadata(ListPageId),
                "a page declared by a loaded dependency .app must be recognised as having metadata to build from");

            var xml = RecordPatches.TryBuildDependencyPageMetadata(ListPageId);
            Assert.NotNull(xml);

            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var root = doc.DocumentElement!;
            Assert.Equal("PageDefinition", root.LocalName);
            Assert.Equal(ListPageId.ToString(), root.GetAttribute("ID"));
            Assert.Equal("DPX Test List Page", root.GetAttribute("Name"));

            var properties = (XmlElement)root.SelectSingleNode("m:Properties", ns)!;
            // This is the actual value NavTestExecution.FindPageType reads
            // (form.MasterPage.PageProperties.PageType) to decide ModalPage vs
            // RequestPage vs FilterPage dispatch — the whole reason this file exists.
            Assert.Equal("List", properties.GetAttribute("PageType"));

            var sourceObject = (XmlElement)properties.SelectSingleNode("m:SourceObject", ns)!;
            Assert.Equal("700", sourceObject.GetAttribute("SourceTable"));
            Assert.Equal("1", sourceObject.GetAttribute("SourceTableTemporary"));

            // Content must be PRESENT (even though empty — see the file header for why: a
            // missing <Content> element NREs one call deeper, inside NCLMetaForm's own
            // post-load control-id-uniqueness check).
            var content = root.SelectSingleNode("m:Content", ns);
            Assert.NotNull(content);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }


    /// <summary>
    /// #2451: a dependency page that declares NO SourceTable — the NavigatePage / wizard /
    /// StandardDialog shape, 167 of them in Base Application 28.1 alone — must still carry an
    /// EMPTY <c>&lt;SourceObject /&gt;</c> element and an <c>&lt;Expressions /&gt;</c> element,
    /// for the same reason the empty <c>&lt;Content /&gt;</c> element above is written
    /// unconditionally: BC dereferences all three without a null check.
    ///
    /// <para><c>MetadataProvider.MergePageAndTable</c> reads
    /// <c>masterPage.PageProperties.SourceObject.SourceTable &gt; 0</c> with no guard on
    /// <c>SourceObject</c>, and <c>LoadExpressionRelationTables</c> does a bare
    /// <c>foreach (… in masterPage.Expressions)</c>. A MISSING element deserializes to null
    /// rather than to an empty one, so omitting either NREs inside BC's own metadata merge —
    /// which <c>RunnerPageInstance.TryCreateRecordless</c> catches, returning null, which
    /// silently demotes the TestPage to the navigation mock whose every action answers
    /// <c>Enabled = true</c> and whose <c>Invoke()</c> is a no-op.</para>
    ///
    /// <para>"Always present" is measured, not assumed: across the 3187 page metadata
    /// documents the real AL compiler emitted into this machine's dependency-compile
    /// sidecars, <c>&lt;SourceObject&gt;</c> appears in 3187 (1114 of them with no
    /// <c>SourceTable</c> attribute at all) and <c>&lt;Properties&gt;</c>,
    /// <c>&lt;Content&gt;</c> and <c>&lt;Expressions&gt;</c> each appear in 3187. Those four
    /// are exactly the elements this synthesizer must never omit.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_PageWithoutSourceTable_StillEmitsEmptySourceObjectAndExpressions()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var xml = RecordPatches.TryBuildDependencyPageMetadata(NavigatePageId);
            Assert.NotNull(xml);

            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var root = doc.DocumentElement!;
            var properties = (XmlElement)root.SelectSingleNode("m:Properties", ns)!;
            Assert.Equal("NavigatePage", properties.GetAttribute("PageType"));

            // Present…
            var sourceObject = (XmlElement?)properties.SelectSingleNode("m:SourceObject", ns);
            Assert.NotNull(sourceObject);
            // …and empty, because the page genuinely has no source table. Writing a
            // SourceTable="0" instead would make MergePageAndTable's `> 0` test read a
            // table id the page does not have, which is a different wrong answer.
            Assert.False(sourceObject!.HasAttribute("SourceTable"));
            Assert.False(sourceObject.HasAttribute("SourceTableTemporary"));

            Assert.NotNull(root.SelectSingleNode("m:Expressions", ns));
            Assert.NotNull(root.SelectSingleNode("m:Content", ns));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The negative half: a page that DOES declare a source table must not be given the
    /// empty element — its <c>SourceTable</c> (and the flags that only mean anything
    /// alongside one) must still be carried through, so the fix above cannot be satisfied by
    /// unconditionally writing an empty <c>&lt;SourceObject /&gt;</c> for every page.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_PageWithSourceTable_KeepsSourceTableOnTheSameElement()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var xml = RecordPatches.TryBuildDependencyPageMetadata(ListPageId);
            Assert.NotNull(xml);

            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var sourceObject = (XmlElement)doc.DocumentElement!
                .SelectSingleNode("m:Properties/m:SourceObject", ns)!;
            Assert.Equal("700", sourceObject.GetAttribute("SourceTable"));
            Assert.Equal("1", sourceObject.GetAttribute("SourceTableTemporary"));
            Assert.NotNull(doc.DocumentElement!.SelectSingleNode("m:Expressions", ns));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryBuildDependencyPageMetadata_UnknownPage_ReturnsNullAndIsNotFlaggedAsHavingMetadata()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            // A page id no loaded dependency describes must get neither a synthesized
            // document nor a false "yes" from the opt-in check RunnerFormInit.
            // ShouldResolveMasterPage relies on — a wrong "yes" here would send BC's own
            // GetMasterPage() down its real path for a page with no XML to load, which
            // fails loudly elsewhere, but the OPT-IN ITSELF must stay honest.
            Assert.False(RecordPatches.HasDependencyPageMetadata(UnknownPageId));
            Assert.Null(RecordPatches.TryBuildDependencyPageMetadata(UnknownPageId));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Issue #2467 — subpage PART reconstruction. A separate app/fixture from the one above:
    // resolving a part's SubFormLink needs real Tables (with named Fields) alongside the
    // Pages, which the shared fixture above deliberately keeps minimal.
    private const int PartsHostPageId = 88123501;
    private const int PartsPartPageId = 88123502;
    private const int PartsHostTableId = 88123520;
    private const int PartsPartTableId = 88123521;
    private const int PartsHostPageNoPartsId = 88123503;

    private const string PartsSymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Tables": [
            {
              "Id": 88123520,
              "Name": "DPX Host Table",
              "Fields": [ { "Id": 1, "Name": "Host Link Field" } ]
            },
            {
              "Id": 88123521,
              "Name": "DPX Part Table",
              "Fields": [ { "Id": 5, "Name": "Part Link Field" }, { "Id": 6, "Name": "Table ID" } ]
            }
          ],
          "Pages": [
            {
              "Id": 88123501,
              "Name": "DPX Test Host Page",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "88123520" }
              ],
              "Controls": [
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "Caption", "Value": "DPX Part Caption" },
                    { "Name": "Editable", "Value": "PartEditableExpr" },
                    { "Name": "SubPageLink", "Value": "\"Part Link Field\" = field(\"Host Link Field\")" }
                  ],
                  "Id": 88123599,
                  "Name": "DPXPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Unresolvable Field\" = field(\"No Such Field\")" }
                  ],
                  "Id": 88123598,
                  "Name": "DPXUnresolvablePart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Table ID\" = const(88123520)" }
                  ],
                  "Id": 88123597,
                  "Name": "DPXConstPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Part Link Field\" = field(\"Host Link Field\"), \"Table ID\" = const(Database::\"DPX Host Table\")" }
                  ],
                  "Id": 88123596,
                  "Name": "DPXConstDatabasePart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Part Link Field\" = const(\"DPX Kind\"::\"On Hold\")" }
                  ],
                  "Id": 88123595,
                  "Name": "DPXConstEnumPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Part Link Field\" = const('SPECIAL')" }
                  ],
                  "Id": 88123594,
                  "Name": "DPXConstQuotedPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Part Link Field\" = filter(Open | \"Bank Acc. Entry Applied\")" }
                  ],
                  "Id": 88123593,
                  "Name": "DPXFilterPart"
                }
              ]
            },
            {
              "Id": 88123502,
              "Name": "DPX Test Part Page",
              "Properties": [
                { "Name": "PageType", "Value": "ListPart" },
                { "Name": "SourceTable", "Value": "88123521" }
              ]
            },
            {
              "Id": 88123503,
              "Name": "DPX Test Host Page Without Parts",
              "Properties": [
                { "Name": "PageType", "Value": "Card" },
                { "Name": "SourceTable", "Value": "88123520" }
              ]
            }
          ]
        }
        """;

    private static XmlElement GetPartControl(XmlDocument doc, XmlNamespaceManager ns, int controlId)
        => (XmlElement)doc.DocumentElement!.SelectSingleNode(
            $"m:Content/m:Containers/m:Controls[@ID='{controlId}']", ns)!;

    /// <summary>
    /// The core #2467 fix: a resolvable FIELD SubPageLink must reconstruct as the numeric
    /// InfopartPageDefinition/SubFormLink shape MockTestPage.SubPageLinks actually consumes
    /// (FieldID / FilterType="FIELD" / FilterValue as numbers), not the AL text
    /// SymbolReference.json states it as. Positive half of RED→GREEN: before this fix,
    /// GetPart(88123599) on this page refused with "testpage-part — could not resolve this
    /// control to a subpage part" because Content was always empty.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_PartWithResolvableFieldLink_EmitsNumericSubFormLink()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, PartsSymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var xml = RecordPatches.TryBuildDependencyPageMetadata(PartsHostPageId);
            Assert.NotNull(xml);

            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
            var xsi = new XmlNamespaceManager(doc.NameTable);

            var part = GetPartControl(doc, ns, 88123599);
            Assert.Equal("InfopartPageDefinition",
                part.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance"));
            Assert.Equal("DPXPart", part.GetAttribute("Name"));
            Assert.Equal(PartsPartPageId.ToString(), part.GetAttribute("PagePartID"));
            Assert.Equal("ENU=DPX Part Caption", part.GetAttribute("CaptionML"));
            Assert.Equal("PartEditableExpr", part.GetAttribute("Editable"));

            var link = (XmlElement)part.SelectSingleNode("m:SubFormLink", ns)!;
            Assert.Equal("4", link.GetAttribute("FilterGroup"));
            Assert.Equal("FIELD", link.GetAttribute("FilterType"));
            // Field 5 is "Part Link Field" on the PART's own table (88123521); field 1 is
            // "Host Link Field" on the HOST's table (88123520) — this is the actual proof
            // that both sides resolved against the RIGHT table, not just "some" table.
            Assert.Equal("5", link.GetAttribute("FieldID"));
            Assert.Equal("1", link.GetAttribute("FilterValue"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Negative half: a SubPageLink field name this run cannot resolve to a numeric id must
    /// NOT be silently dropped (which would leave the part unfiltered — showing every row of
    /// the child table rather than only the parent's, a wrong answer loud-failures.md
    /// forbids) and must NOT be silently defaulted to some guessed number. It has to come out
    /// shaped so MockTestPage.SubPageLinks' own existing check — <c>int.TryParse</c> on
    /// FilterValue — fails and refuses by name.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_PartWithUnresolvableFieldLink_EmitsNonNumericFilterValue()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, PartsSymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var xml = RecordPatches.TryBuildDependencyPageMetadata(PartsHostPageId);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var part = GetPartControl(doc, ns, 88123598);
            var link = (XmlElement)part.SelectSingleNode("m:SubFormLink", ns)!;
            Assert.Equal("FIELD", link.GetAttribute("FilterType"));
            Assert.False(int.TryParse(link.GetAttribute("FilterValue"), out _),
                "an unresolved field name must not parse as a number — that is what makes " +
                "MockTestPage.SubPageLinks refuse it by name instead of silently unfiltering the part");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A CONST-kind SubPageLink (10.7% of Base Application 28.1's SubPageLink entries,
    /// measured) is reproduced with its REAL FilterType, and a numeric literal passes
    /// through as written — the representation BC's own compiler emits, which
    /// MockTestPage.SubPageLinks applies as a single-value filter on the part's field (#2469).
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_PartWithConstLink_PreservesConstFilterType()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, PartsSymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var xml = RecordPatches.TryBuildDependencyPageMetadata(PartsHostPageId);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var part = GetPartControl(doc, ns, 88123597);
            var link = (XmlElement)part.SelectSingleNode("m:SubFormLink", ns)!;
            Assert.Equal("CONST", link.GetAttribute("FilterType"));
            Assert.Equal("88123520", link.GetAttribute("FilterValue"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// #2469 — the AL-text shapes a dependency's <c>const(...)</c> arrives in must reach the
    /// part as the representation BC's compiler would have written for a source-compiled
    /// page (measured on 28.1: <c>Database::X</c> compiles to the table id, a quoted text
    /// literal to its bare text), or an equivalent BC's filter grammar reads the same way
    /// (an enum member by NAME rather than ordinal). Each arm asserts a specific value that
    /// only the right normalisation produces; the raw AL text passing through would fail all
    /// three.
    /// </summary>
    [Theory]
    [InlineData(88123596, "88123520", "6")]      // "Table ID"       = const(Database::"DPX Host Table") -> the host table's id
    [InlineData(88123595, "On Hold", "5")]       // "Part Link Field" = const("DPX Kind"::"On Hold")     -> the member name, unquoted
    [InlineData(88123594, "SPECIAL", "5")]       // "Part Link Field" = const('SPECIAL')                 -> the bare text
    public void TryBuildDependencyPageMetadata_ConstLink_NormalisesToCompilerRepresentation(int controlId, string expected, string partFieldId)
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, PartsSymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var xml = RecordPatches.TryBuildDependencyPageMetadata(PartsHostPageId);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var part = GetPartControl(doc, ns, controlId);
            var links = part.SelectNodes("m:SubFormLink", ns)!.Cast<XmlElement>().ToList();
            var constLink = Assert.Single(links, l => l.GetAttribute("FilterType") == "CONST");
            Assert.Equal(expected, constLink.GetAttribute("FilterValue"));
            // The part-side field resolved to the PART table's own field number for every
            // kind — 0 is what MockTestPage.SubPageLinks refuses by name, and the fixture
            // declares both fields these links name ("Part Link Field" 5, "Table ID" 6), so a
            // 0 here would be a resolution failure rather than a link naming a field that
            // genuinely does not exist.
            Assert.Equal(partFieldId, constLink.GetAttribute("FieldID"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// #2469 — a <c>filter(...)</c> link keeps FilterType FILTER and has its AL-quoted
    /// identifiers re-quoted for BC's filter grammar, exactly as the CalcFormula
    /// <c>filter(...)</c> path does (#2305): the tokenizer has no case for <c>"</c>, so
    /// <c>"Bank Acc. Entry Applied"</c> carried through verbatim would be a 25-character
    /// literal matching no option member.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_FilterLink_RequotesIdentifiersForFilterGrammar()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, PartsSymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var xml = RecordPatches.TryBuildDependencyPageMetadata(PartsHostPageId);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var part = GetPartControl(doc, ns, 88123593);
            var link = (XmlElement)part.SelectSingleNode("m:SubFormLink", ns)!;
            Assert.Equal("FILTER", link.GetAttribute("FilterType"));
            Assert.Equal("5", link.GetAttribute("FieldID"));
            Assert.Equal("Open | 'Bank Acc. Entry Applied'", link.GetAttribute("FilterValue"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A page with no parts at all must keep the pre-#2467 shape: Content present but with
    /// no Containers child — never an empty <c>&lt;Containers&gt;</c> wrapper, which would be
    /// a needless divergence from what the real compiler emits for such a page.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_PageWithoutParts_ContentHasNoContainers()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, PartsSymbolReference);
            RecordPatches.AddBcAppPath(appPath);

            var xml = RecordPatches.TryBuildDependencyPageMetadata(PartsHostPageNoPartsId);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var content = (XmlElement)doc.DocumentElement!.SelectSingleNode("m:Content", ns)!;
            Assert.Null(content.SelectSingleNode("m:Containers", ns));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// #2550: the three flags the AL compiler writes on &lt;SourceObject&gt; alongside
    /// SourceTable were dropped, so a page shipping precompiled in a dependency .app answered
    /// as if it declared none of them.
    ///
    /// <para>AutoSplitKey is the one with consequences. <c>RunnerPageInstance.NeedsAutoSplitKey</c>
    /// reads <c>form.MasterPage.PageProperties.SourceObject.AutoSplitKey</c>, so a missing
    /// attribute read false, BC's client half of AutoSplitKey silently did not run, and per the
    /// note in <c>MockTestPage</c> the first new row then lands at line no. 0 and the second
    /// fails on a duplicate primary key.</para>
    ///
    /// <para>The attribute names and value spelling are measured, not guessed: compiling a page
    /// that declares all three and reading back the metadata the BC 28.1 compiler captured for
    /// it gives
    /// <c>&lt;SourceObject AutoSplitKey="1" DelayedInsert="1" MultipleNewLines="1" SourceTable="65940" /&gt;</c>.
    /// Base Application 28.1's own SymbolReference.json states AutoSplitKey on 234 of its 2610
    /// pages, MultipleNewLines on 116 and DelayedInsert on 303.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_CarriesTheSourceObjectFlagsTheSymbolFileStates()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            var sourceObject = ReadSourceObject(SplitKeyPageId);

            Assert.Equal("1", sourceObject.GetAttribute("AutoSplitKey"));
            Assert.Equal("1", sourceObject.GetAttribute("MultipleNewLines"));
            Assert.Equal("1", sourceObject.GetAttribute("DelayedInsert"));
            // The flags must not have displaced what was already there.
            Assert.Equal("701", sourceObject.GetAttribute("SourceTable"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The other direction, and the one that makes the test above mean something: all three
    /// default to FALSE in AL, so a page whose symbol file states none of them must carry no
    /// such attribute at all. Writing them unconditionally — as "0", or worse as "1" — would
    /// make every dependency page claim AutoSplitKey, which is a different wrong answer from
    /// the one being fixed.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_OmitsTheSourceObjectFlagsAPageDoesNotDeclare()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SymbolReference));

            var sourceObject = ReadSourceObject(PlainLinesPageId);

            Assert.False(sourceObject.HasAttribute("AutoSplitKey"), "AutoSplitKey must be absent, not \"0\"");
            Assert.False(sourceObject.HasAttribute("MultipleNewLines"), "MultipleNewLines must be absent");
            Assert.False(sourceObject.HasAttribute("DelayedInsert"), "DelayedInsert must be absent");
            Assert.Equal("701", sourceObject.GetAttribute("SourceTable"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Issue #2820 — SourceTableView reconstruction. Its own app/fixture for the same reason
    // the parts one above has one: resolving a view's field NAMES to ids needs real Tables
    // with named Fields.
    private const int ViewPageId = 88123601;
    private const int NoViewPageId = 88123602;
    private const int UnresolvableViewPageId = 88123603;
    private const int WhereOnlyViewPageId = 88123604;

    private const string ViewSymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Tables": [
            {
              "Id": 88123620,
              "Name": "DPX View Table",
              "Fields": [
                { "Id": 1, "Name": "No." },
                { "Id": 2, "Name": "Bucket" },
                { "Id": 3, "Name": "Price Type" }
              ]
            }
          ],
          "Pages": [
            {
              "Id": 88123601,
              "Name": "DPX View Page",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" },
                { "Name": "SourceTableView", "Value": "sorting(Bucket, \"No.\")\r\n                      order(descending)\r\n                      where(\"Price Type\" = const(Sale),\r\n                            Bucket = filter(1|2))" }
              ]
            },
            {
              "Id": 88123602,
              "Name": "DPX No View Page",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" }
              ]
            },
            {
              "Id": 88123603,
              "Name": "DPX Unresolvable View Page",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" },
                { "Name": "SourceTableView", "Value": "where(\"No Such Field\" = const(Sale))" }
              ]
            },
            {
              "Id": 88123604,
              "Name": "DPX Where Only View Page",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" },
                { "Name": "SourceTableView", "Value": "where(Bucket = const(7))" }
              ]
            }
          ]
        }
        """;

    private static XmlElement ReadSourceObjectFor(int pageId)
    {
        var xml = RecordPatches.TryBuildDependencyPageMetadata(pageId);
        Assert.NotNull(xml);
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        return (XmlElement)doc.DocumentElement!.SelectSingleNode("m:Properties/m:SourceObject", ns)!;
    }

    private static XmlNamespaceManager MetaNs(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
        return ns;
    }

    /// <summary>
    /// #2820: a page declared only by a precompiled dependency .app got no
    /// <c>&lt;SourceTableView&gt;</c> at all, so BC's own <c>NavForm.ApplySourceTableView</c>
    /// found nothing to apply and the page opened unfiltered. The where(...) entries must
    /// reconstruct as the <c>&lt;TableFilters&gt;</c> shape ApplySourceTableView consumes:
    /// resolved numeric FieldID, CONST/FILTER, and FilterGroup 2 — the group BC sets the
    /// record to while applying them, and the group Base Application page 7016's OnOpenPage
    /// reads back out.
    ///
    /// <para>An enum <c>const(Member)</c> is written as the member NAME where the compiler
    /// writes its ordinal; that equivalence (BC's filter grammar resolves an option member by
    /// either) is the one NormalizeConstLinkValue already documents for SubPageLinks, and the
    /// runner has no ordinal table for a dependency's fields at this point.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_SourceTableView_EmitsTableFiltersInFilterGroup2()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, ViewSymbolReference));

            var sourceObject = ReadSourceObjectFor(ViewPageId);
            var ns = MetaNs(sourceObject.OwnerDocument!);
            var view = (XmlElement?)sourceObject.SelectSingleNode("m:SourceTableView", ns);
            Assert.NotNull(view);

            var filters = view!.SelectNodes("m:TableFilters", ns)!.Cast<XmlElement>().ToList();
            Assert.Equal(2, filters.Count);

            // "Price Type" = const(Sale) — field 3 of the fixture's table.
            Assert.Equal("2", filters[0].GetAttribute("FilterGroup"));
            Assert.Equal("3", filters[0].GetAttribute("FieldID"));
            Assert.Equal("CONST", filters[0].GetAttribute("FilterType"));
            Assert.Equal("Sale", filters[0].GetAttribute("FilterValue"));

            // Bucket = filter(1|2) — field 2, and a FILTER expression carried through.
            Assert.Equal("2", filters[1].GetAttribute("FilterGroup"));
            Assert.Equal("2", filters[1].GetAttribute("FieldID"));
            Assert.Equal("FILTER", filters[1].GetAttribute("FilterType"));
            Assert.Equal("1|2", filters[1].GetAttribute("FilterValue"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The sorting half. <c>sorting(Bucket, "No.") order(descending)</c> must reconstruct as
    /// BC's own <c>&lt;Sorting&gt;</c> shape, whose <c>KeyFields</c> is the
    /// <c>Field&lt;id&gt;</c> spelling <c>MetaTable.GetKeyFieldIds</c> parses, in the order the
    /// view declares — measured by compiling exactly that view on BC 28.1 and reading back
    /// the metadata the compiler captured: <c>KeyFields="Field2,Field1"
    /// KeyFieldsSetByView="1" AscendingSetByView="1" Ascending="0"</c>.
    ///
    /// <para>The two <c>*SetByView</c> flags are what ApplySourceTableView gates on, so
    /// getting them wrong is silent: the page keeps its default key and ascending order and
    /// nothing reports that the view was ignored.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_SourceTableViewSortingAndOrder_EmitsKeyFieldIdsAndDescending()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, ViewSymbolReference));

            var sourceObject = ReadSourceObjectFor(ViewPageId);
            var ns = MetaNs(sourceObject.OwnerDocument!);
            var sorting = (XmlElement?)sourceObject.SelectSingleNode("m:SourceTableView/m:Sorting", ns);
            Assert.NotNull(sorting);

            Assert.Equal("Field2,Field1", sorting!.GetAttribute("KeyFields"));
            Assert.Equal("1", sorting.GetAttribute("KeyFieldsSetByView"));
            Assert.Equal("1", sorting.GetAttribute("AscendingSetByView"));
            Assert.Equal("0", sorting.GetAttribute("Ascending"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The direction that makes the two above mean something: a page stating no
    /// SourceTableView must carry no <c>&lt;SourceTableView&gt;</c> element. Writing an empty
    /// one unconditionally would give every dependency page a ViewDefinition whose empty
    /// TableFilters list ApplySourceTableView walks — harmless today, but it would also make
    /// the positive tests above pass for a synthesizer that emits the element and nothing in
    /// it.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_PageWithoutSourceTableView_EmitsNone()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, ViewSymbolReference));

            var sourceObject = ReadSourceObjectFor(NoViewPageId);
            var ns = MetaNs(sourceObject.OwnerDocument!);
            Assert.Null(sourceObject.SelectSingleNode("m:SourceTableView", ns));
            // …and the element it does carry is untouched.
            Assert.Equal("88123620", sourceObject.GetAttribute("SourceTable"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A view with a <c>where(...)</c> and no <c>sorting(...)</c>/<c>order(...)</c> gets no
    /// <c>&lt;Sorting&gt;</c> element: ApplySourceTableView reads that element only through
    /// its two <c>*SetByView</c> flags, so one with neither set can do nothing, and 178 of
    /// Base Application 28.1's 386 SourceTableView pages declare sorting at all.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_SourceTableViewWithoutSorting_EmitsFiltersButNoSortingElement()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, ViewSymbolReference));

            var sourceObject = ReadSourceObjectFor(WhereOnlyViewPageId);
            var ns = MetaNs(sourceObject.OwnerDocument!);
            var view = (XmlElement?)sourceObject.SelectSingleNode("m:SourceTableView", ns);
            Assert.NotNull(view);

            Assert.Null(view!.SelectSingleNode("m:Sorting", ns));
            var filter = (XmlElement)view.SelectSingleNode("m:TableFilters", ns)!;
            Assert.Equal("2", filter.GetAttribute("FieldID"));
            Assert.Equal("CONST", filter.GetAttribute("FilterType"));
            Assert.Equal("7", filter.GetAttribute("FilterValue"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A view field name this run cannot resolve is written as <c>FieldID="0"</c>, so BC's own
    /// <c>MetaTable.GetFieldByNo(0)</c> refuses with NavNCLFieldNotFoundException when the page
    /// opens. Dropping the entry instead would show every row of the table — the exact defect
    /// #2820 is about — and do it silently; the same "fail loudly rather than show unfiltered
    /// rows" choice EmitSubFormLinkXml already makes for an unresolvable part link.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_UnresolvableViewField_KeepsTheFilterWithFieldIdZero()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, ViewSymbolReference));

            var sourceObject = ReadSourceObjectFor(UnresolvableViewPageId);
            var ns = MetaNs(sourceObject.OwnerDocument!);
            var filters = sourceObject.SelectNodes("m:SourceTableView/m:TableFilters", ns)!
                .Cast<XmlElement>().ToList();

            Assert.Single(filters);
            Assert.Equal("0", filters[0].GetAttribute("FieldID"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static XmlElement ReadSourceObject(int pageId)
    {
        var xml = RecordPatches.TryBuildDependencyPageMetadata(pageId);
        Assert.NotNull(xml);

        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

        var properties = (XmlElement)doc.DocumentElement!.SelectSingleNode("m:Properties", ns)!;
        return (XmlElement)properties.SelectSingleNode("m:SourceObject", ns)!;
    }
}