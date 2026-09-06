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
              "Fields": [ { "Id": 1, "Name": "Host Link Field" }, { "Id": 2, "Name": "Chargeable Filter" } ]
            },
            {
              "Id": 88123521,
              "Name": "DPX Part Table",
              "Fields": [
                { "Id": 5, "Name": "Part Link Field" },
                { "Id": 6, "Name": "Table ID" },
                { "Id": 7, "Name": "Chargeable Filter" }
              ]
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
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Part Link Field\" = field(\"Host Link Field\"), \"Table ID\" = valuefilter(1)" }
                  ],
                  "Id": 88123592,
                  "Name": "DPXPartialLinkPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Part Link Field\" = field(\"Host Link Field\"),\r\n#if not CLEAN25\r\n                              \"Service Zone Filter\" = field(\"Service Zone Filter\"),\r\n#endif\r\n                              \"Chargeable Filter\" = field(\"Chargeable Filter\")" }
                  ],
                  "Id": 88123591,
                  "Name": "DPXDirectiveCompiledOutPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Part Link Field\" = field(\"Host Link Field\"),\r\n#if not CLEAN25\r\n                              \"Table ID\" = const(88123520),\r\n#endif\r\n                              \"Chargeable Filter\" = field(\"Chargeable Filter\")" }
                  ],
                  "Id": 88123590,
                  "Name": "DPXDirectiveCompiledInPart"
                },
                {
                  "Kind": 6,
                  "RelatedPagePartId": { "Name": "", "Id": 88123502 },
                  "Properties": [
                    { "Name": "SubPageLink", "Value": "\"Part Link Field\" = field(\"Host Link Field\"),\r\n#if not CLEAN25\r\n                              \"Service Zone Filter\" = field(\"Service Zone Filter\"),\r\n                              \"Second Absent Field\" = field(\"Host Link Field\"),\r\n#endif\r\n                              \"Chargeable Filter\" = field(\"Chargeable Filter\")" }
                  ],
                  "Id": 88123589,
                  "Name": "DPXDirectiveBlockPart"
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
    /// #2978's sibling. <c>ParseSubPageLink</c> makes the same fail-open assumption
    /// <c>ParseSourceTableView</c> did, with the same <c>continue</c> and the same regex: an
    /// entry it cannot read is dropped, and the remaining entries ship as if they were the
    /// whole link. A two-condition SubPageLink reduced to one condition filters the part
    /// LESS, so the subpage shows rows the host row does not own — the same class of wrong
    /// answer as a widened page view, one level down.
    ///
    /// <para>The refusal channel is the one <c>EmitSubFormLinkXml</c> already documents:
    /// <c>FieldID="0"</c>, which <c>MockTestPage.SubPageLinks</c> refuses by name for every
    /// kind. Two links, not one, and the unreadable one refuses.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_UnreadableSubPageLinkEntry_KeepsARefusingLinkRatherThanWideningThePart()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, PartsSymbolReference));

            var xml = RecordPatches.TryBuildDependencyPageMetadata(PartsHostPageId);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var part = GetPartControl(doc, ns, 88123592);
            var links = part.SelectNodes("m:SubFormLink", ns)!.Cast<XmlElement>().ToList();

            // BEFORE #2978 this was 1 — the part filtered on "Part Link Field" alone.
            Assert.Equal(2, links.Count);

            Assert.Equal("5", links[0].GetAttribute("FieldID"));
            Assert.Equal("FIELD", links[0].GetAttribute("FilterType"));
            Assert.Equal("1", links[0].GetAttribute("FilterValue"));

            Assert.Equal("0", links[1].GetAttribute("FieldID"));
            Assert.Equal("4", links[1].GetAttribute("FilterGroup"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// #2978, the half that is not hypothetical. The AL compiler records a property's SOURCE
    /// text in SymbolReference.json, PREPROCESSOR DIRECTIVES AND ALL, and BC 27.5's Base
    /// Application ships three subpage parts that carry one — page 76 "Resource Card"
    /// Control1906609707, page 77 "Resource List" Control1906609707 and Control1907012907, all
    /// three verbatim:
    /// <code>
    /// "No." = field("No."),
    ///                               "Unit of Measure Filter" = field("Unit of Measure Filter"),
    /// #if not CLEAN25
    ///                               "Service Zone Filter" = field("Service Zone Filter"),
    /// #endif
    ///                               "Chargeable Filter" = field("Chargeable Filter")
    /// </code>
    /// <para>Comma-splitting leaves two entries the regex cannot read — one prefixed
    /// <c>#if not CLEAN25</c>, one prefixed <c>#endif</c> — and both were dropped. So those
    /// three parts filtered on TWO of their four conditions and showed rows the host resource
    /// does not own. That is not the unmeasured tail this issue was filed about; it is
    /// shipping, on a BC version this runner's own CI matrix covers.</para>
    ///
    /// <para>Which of the two entries is really in the compiled app is measured, not guessed:
    /// BC 27.5's own symbol file states table <c>Resource</c> with 58 fields and NO "Service
    /// Zone Filter", so <c>CLEAN25</c> WAS defined when Microsoft compiled it and the guarded
    /// entry was compiled out — and BC 28.1, where the directive has been deleted from the
    /// source, carries exactly the three unconditional entries and no "Service Zone Filter".
    /// So the <c>#endif</c> entry must parse and apply, and the <c>#if</c> entry must be
    /// omitted rather than turned into a refusal: refusing a page over a link the app does not
    /// contain is a wrong answer in the other direction.</para>
    ///
    /// <para>Measured across every extension of BC 27.5 and 28.1 W1 — 7,646 pages, 3,655
    /// SubPageLink entries, 981 SourceTableView pages with 547 where-entries — those six
    /// entries on those three controls are the ONLY ones in either version that fail to
    /// parse, and no SourceTableView carries a directive at all.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_DirectiveGuardedLinkCompiledOut_AppliesTheEntryAfterEndifAndOmitsTheGuardedOne()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, PartsSymbolReference));

            var xml = RecordPatches.TryBuildDependencyPageMetadata(PartsHostPageId);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var part = GetPartControl(doc, ns, 88123591);
            var links = part.SelectNodes("m:SubFormLink", ns)!.Cast<XmlElement>().ToList();

            // BEFORE #2978 this was 1: the "#endif …Chargeable Filter" entry was dropped along
            // with the guarded one, and the part showed every Chargeable Filter value.
            Assert.Equal(2, links.Count);

            Assert.Equal("5", links[0].GetAttribute("FieldID"));      // "Part Link Field"
            Assert.Equal("1", links[0].GetAttribute("FilterValue"));  // <- "Host Link Field"

            // The entry that only LOOKED unreadable because #endif preceded it.
            Assert.Equal("7", links[1].GetAttribute("FieldID"));      // "Chargeable Filter"
            Assert.Equal("FIELD", links[1].GetAttribute("FilterType"));
            Assert.Equal("2", links[1].GetAttribute("FilterValue"));  // <- host "Chargeable Filter"

            // …and NOT as a refusal: the guarded entry names a field this app does not have,
            // which is the app saying the directive compiled it out.
            Assert.DoesNotContain(links, l => l.GetAttribute("FieldID") == "0");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The direction that stops the test above from passing for an implementation that just
    /// throws every conditional entry away: a <c>#if</c>-guarded entry whose fields this app
    /// DOES have is in the app, and must filter. Same three-entry link, same directive, only
    /// the guarded entry's field names differ.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_DirectiveGuardedLinkCompiledIn_KeepsFilteringOnIt()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, PartsSymbolReference));

            var xml = RecordPatches.TryBuildDependencyPageMetadata(PartsHostPageId);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var part = GetPartControl(doc, ns, 88123590);
            var links = part.SelectNodes("m:SubFormLink", ns)!.Cast<XmlElement>().ToList();

            Assert.Equal(3, links.Count);
            Assert.Equal("5", links[0].GetAttribute("FieldID"));

            // The guarded entry, applied: "Table ID" = const(88123520), field 6 of the part.
            Assert.Equal("6", links[1].GetAttribute("FieldID"));
            Assert.Equal("CONST", links[1].GetAttribute("FilterType"));
            Assert.Equal("88123520", links[1].GetAttribute("FilterValue"));

            Assert.Equal("7", links[2].GetAttribute("FieldID"));
            Assert.DoesNotContain(links, l => l.GetAttribute("FieldID") == "0");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Conditional-ness is a property of the BLOCK, not of the entry that happens to carry the
    /// directive text. Comma-splitting only puts <c>#if not CLEAN25</c> on the FIRST guarded
    /// entry, so a second entry inside the same block carries no <c>#</c> line of its own —
    /// and reading it as unconditional would turn its absent field into a refusal, i.e. a page
    /// that will not open because of AL the compiler removed.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_SecondEntryInsideTheSameDirectiveBlock_IsConditionalToo()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, PartsSymbolReference));

            var xml = RecordPatches.TryBuildDependencyPageMetadata(PartsHostPageId);
            var doc = new XmlDocument();
            doc.LoadXml(xml!);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("m", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");

            var part = GetPartControl(doc, ns, 88123589);
            var links = part.SelectNodes("m:SubFormLink", ns)!.Cast<XmlElement>().ToList();

            Assert.Equal(2, links.Count);
            Assert.Equal("5", links[0].GetAttribute("FieldID"));
            Assert.Equal("7", links[1].GetAttribute("FieldID"));
            Assert.DoesNotContain(links, l => l.GetAttribute("FieldID") == "0");
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
    private const int ViewPlusPropsPageId = 88123605;
    private const int PartialViewPageId = 88123606;
    private const int UnbalancedViewPageId = 88123607;

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
            },
            {
              "Id": 88123605,
              "Name": "DPX View Plus Props Page",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" },
                { "Name": "LinksAllowed", "Value": "0" },
                { "Name": "PopulateAllFields", "Value": "1" },
                { "Name": "SourceTableView", "Value": "where(Bucket = const(7))" }
              ]
            },
            {
              "Id": 88123606,
              "Name": "DPX Partial View Page",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" },
                { "Name": "SourceTableView", "Value": "where(Bucket = const(7), \"Price Type\" = valuefilter(Sale))" }
              ]
            },
            {
              "Id": 88123607,
              "Name": "DPX Unbalanced View Page",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" },
                { "Name": "SourceTableView", "Value": "sorting(Bucket) where(Bucket = const(7)" }
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

    /// <summary>
    /// #2978 — the fail-OPEN half of the same feature. A <c>where(...)</c> entry
    /// <c>ParseSourceTableView</c> cannot read used to be dropped with a
    /// <c>Console.Error.WriteLine</c> and a <c>continue</c>, and the page shipped the
    /// remaining entries as if they were the whole view. That is WIDER than the view the
    /// page declares: rows the real view excludes are shown, a test asserting over them
    /// passes against a record set BC would not give, and nothing reports it — the stderr
    /// line is written on a symbol-cache MISS and lost on every warm run after.
    ///
    /// <para>So an entry that could not be read keeps its place in the emitted
    /// <c>&lt;TableFilters&gt;</c> list with <c>FieldID="0"</c>: BC's own
    /// <c>MetaTable.GetFieldByNo(0)</c> refuses it with NavNCLFieldNotFoundException when the
    /// page opens, which is exactly the fail-closed choice the unresolvable-FIELD-NAME case
    /// above already makes. The page refuses to open rather than open on the wrong rows.</para>
    ///
    /// <para>The trigger is a kind keyword outside <c>field</c>/<c>const</c>/<c>filter</c>,
    /// standing in for "AL text this parser has not been taught". Whether that exact spelling
    /// compiles is beside the point: the claim under test is what the runner does when it
    /// cannot read an entry, not which entries it can read. Measured on BC 28.1's Base
    /// Application, System Application, Business Foundation and Application: 2845 pages, 417
    /// declaring a SourceTableView, 255 where-entries and 1357 SubPageLink entries, and every
    /// single one parses — so this path is unreachable for what Microsoft ships today, and
    /// this change cannot alter any of it.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_UnreadableViewEntry_KeepsARefusingFilterRatherThanWideningTheView()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, ViewSymbolReference));

            var sourceObject = ReadSourceObjectFor(PartialViewPageId);
            var ns = MetaNs(sourceObject.OwnerDocument!);
            var filters = sourceObject.SelectNodes("m:SourceTableView/m:TableFilters", ns)!
                .Cast<XmlElement>().ToList();

            // BEFORE #2978 this was Single(filters) — Bucket alone, and the page opened on
            // every "Price Type".
            Assert.Equal(2, filters.Count);

            // The entry that DID parse is untouched: field 2 of the fixture table, CONST 7.
            Assert.Equal("2", filters[0].GetAttribute("FieldID"));
            Assert.Equal("CONST", filters[0].GetAttribute("FilterType"));
            Assert.Equal("7", filters[0].GetAttribute("FilterValue"));

            // The one that did not is present and refusing, in the same filter group.
            Assert.Equal("0", filters[1].GetAttribute("FieldID"));
            Assert.Equal("2", filters[1].GetAttribute("FilterGroup"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// #2978, the clause-level half: a <c>sorting(...) where(...</c> whose parenthesis never
    /// closes made <c>MatchingCloseParen</c> return -1 and the whole <c>where</c> clause
    /// vanish, while the <c>sorting</c> beside it still applied. The page then came up SORTED
    /// as declared and UNFILTERED — the most convincing possible wrong answer, because the
    /// half that is easy to eyeball is right.
    ///
    /// <para>Only the case where NO clause at all could be read stays a <c>return null</c>,
    /// and that one is safe: the caller emits no <c>&lt;SourceTableView&gt;</c> element, which
    /// is the pre-#2820 state rather than a manufactured narrower one.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_UnbalancedViewClause_RefusesRatherThanSortingAnUnfilteredPage()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, ViewSymbolReference));

            var sourceObject = ReadSourceObjectFor(UnbalancedViewPageId);
            var ns = MetaNs(sourceObject.OwnerDocument!);

            // The sorting half still reads — that is what made the drop convincing.
            var sorting = (XmlElement?)sourceObject.SelectSingleNode("m:SourceTableView/m:Sorting", ns);
            Assert.NotNull(sorting);
            Assert.Equal("Field2", sorting!.GetAttribute("KeyFields"));

            // BEFORE #2978 there were no TableFilters at all here.
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

    // Issue #2860 — the five further <SourceObject> properties the symbol file states and the
    // synthesized XML dropped. Own app/fixture, because the point of most of these tests is
    // exactly which attributes are ABSENT, and the shared fixtures above already state
    // properties of their own.
    //
    // WHAT "CORRECT" MEANS HERE, MEASURED
    //   The rule this fixture pins is not "write the AL default when the file says nothing" —
    //   it is "write the attribute if and only if the symbol file states it, with the value
    //   the symbol file states". That is what the real AL compiler does, measured on BC 28.1
    //   by compiling four pages and reading back the metadata it captured for each
    //   (AL_RUNNER_TRACE_PAGE_METADATA=2):
    //
    //     LinksAllowed = false; ShowFilter = false; SaveValues = true;
    //     PopulateAllFields = true; DataCaptionFields = "No.", Descr;
    //       -> <SourceObject DataCaptionFields="1,3" LinksAllowed="0" PopulateAllFields="1"
    //                        SaveValues="1" ShowFilter="0" SourceTable="64900" />
    //
    //     LinksAllowed = true; ShowFilter = true; SaveValues = false; PopulateAllFields = false;
    //       -> <SourceObject LinksAllowed="1" PopulateAllFields="0" SaveValues="0"
    //                        ShowFilter="1" SourceTable="64900" />      // AL DEFAULTS, still written
    //
    //     (declares none of them)
    //       -> <SourceObject SourceTable="64900" />
    //
    //     no SourceTable at all; LinksAllowed = false; ShowFilter = false; SaveValues = true;
    //       -> <SourceObject LinksAllowed="0" SaveValues="1" ShowFilter="0" />
    //
    //   The second and fourth lines are the two an "obvious" implementation gets wrong: a
    //   plain bool defaulting to the AL default cannot tell "declared as the default" from
    //   "not declared", and nesting the attributes alongside SourceTable drops them for the
    //   30 Base Application 28.1 pages that declare one of the five without a source table.
    //   DataCaptionFields is already FIELD NUMBERS in the symbol file (measured: all 381
    //   Base Application 28.1 pages stating it state a comma-separated numeric list), which
    //   is the same representation the compiler writes.
    private const int SodAllStatedPageId = 88123701;
    private const int SodAlDefaultsPageId = 88123702;
    private const int SodStatesNonePageId = 88123703;
    private const int SodNoSourceTablePageId = 88123704;
    private const int SodBadCaptionFieldsPageId = 88123705;
    private const int SodUnreadableBoolPageId = 88123706;

    private const string SourceObjectPropsSymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "Pages": [
            {
              "Id": 88123701,
              "Name": "DPX SOD All Stated",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" },
                { "Name": "LinksAllowed", "Value": "0" },
                { "Name": "ShowFilter", "Value": "0" },
                { "Name": "SaveValues", "Value": "1" },
                { "Name": "PopulateAllFields", "Value": "1" },
                { "Name": "DataCaptionFields", "Value": "1,3" }
              ]
            },
            {
              "Id": 88123702,
              "Name": "DPX SOD AL Defaults",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" },
                { "Name": "LinksAllowed", "Value": "1" },
                { "Name": "ShowFilter", "Value": "1" },
                { "Name": "SaveValues", "Value": "0" },
                { "Name": "PopulateAllFields", "Value": "0" }
              ]
            },
            {
              "Id": 88123703,
              "Name": "DPX SOD States None",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" }
              ]
            },
            {
              "Id": 88123704,
              "Name": "DPX SOD No Source Table",
              "Properties": [
                { "Name": "PageType", "Value": "NavigatePage" },
                { "Name": "LinksAllowed", "Value": "0" },
                { "Name": "ShowFilter", "Value": "0" },
                { "Name": "SaveValues", "Value": "1" }
              ]
            },
            {
              "Id": 88123705,
              "Name": "DPX SOD Bad Caption Fields",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" },
                { "Name": "DataCaptionFields", "Value": "\"No.\",Descr" }
              ]
            },
            {
              "Id": 88123706,
              "Name": "DPX SOD Unreadable Bool",
              "Properties": [
                { "Name": "PageType", "Value": "List" },
                { "Name": "SourceTable", "Value": "88123620" },
                { "Name": "PopulateAllFields", "Value": "yes" },
                { "Name": "LinksAllowed", "Value": "0" }
              ]
            }
          ]
        }
        """;

    private static readonly string[] SodProperties =
        { "LinksAllowed", "ShowFilter", "SaveValues", "PopulateAllFields", "DataCaptionFields" };

    /// <summary>
    /// The core #2860 claim: every one of the five properties the symbol file states reaches
    /// the synthesized <c>&lt;SourceObject&gt;</c> verbatim.
    ///
    /// <para><c>PopulateAllFields</c> is the one with teeth. BC's own
    /// <c>SourceObjectDefinition(XmlNode)</c> constructor initialises it to <c>false</c>
    /// before reading attributes, so a dropped attribute is indistinguishable from a declared
    /// <c>PopulateAllFields = false</c> — and <c>NavForm.NewRecordAsync</c> reads
    /// <c>MasterPage.PageProperties.SourceObject.PopulateAllFields</c> on EVERY new row, as
    /// the <c>includeNonPrimaryKeyFields</c> argument to
    /// <c>NavRecord.InitializeFieldsFromFilters</c>. For the 46 Base Application 28.1 pages
    /// that declare it true, the runner answered <c>false</c>, and a new row was initialised
    /// from primary-key filters only where BC initialises it from all of them.</para>
    ///
    /// <para><c>SaveValues</c> has a live reader too — <c>NavForm.InitializeFromMetadata</c>
    /// assigns <c>saveValues = masterPage.PageProperties.SourceObject.SaveValues</c>, which
    /// gates <c>ApplySourceTableViewAndSavedValuesAsync</c>'s call to
    /// <c>ApplyLatestValuesAsync()</c> on the <c>NavForm.OpenForm()</c> route
    /// RunnerModalDispatch takes. Carrying it is not a new risk: a page the runner
    /// SOURCE-compiles already gets <c>SaveValues="1"</c> from the real compiler and opens
    /// and closes through that route today. <c>LinksAllowed</c>, <c>ShowFilter</c> and
    /// <c>DataCaptionFields</c> are read in Ncl only by <c>PageDataProvider</c>, the data
    /// provider behind the Page Metadata (2000000138) system table — which this runner
    /// substitutes wholesale, so they have no reader here YET; they are carried because the
    /// value is the symbol file's own, not because a reader was found for each.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_SourceObjectPropertiesStatedBySymbolFile_AreCarriedVerbatim()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SourceObjectPropsSymbolReference));

            var sourceObject = ReadSourceObjectFor(SodAllStatedPageId);

            Assert.Equal("0", sourceObject.GetAttribute("LinksAllowed"));
            Assert.Equal("0", sourceObject.GetAttribute("ShowFilter"));
            Assert.Equal("1", sourceObject.GetAttribute("SaveValues"));
            Assert.Equal("1", sourceObject.GetAttribute("PopulateAllFields"));
            // Field NUMBERS, exactly as both the symbol file and the compiler state them —
            // writing the AL source's field NAMES here would be a value BC cannot use.
            Assert.Equal("1,3", sourceObject.GetAttribute("DataCaptionFields"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The half that a <c>bool</c> field with an AL default silently gets wrong: a page
    /// STATING the AL default must still carry the attribute, because "stated" and
    /// "not stated" are different documents to BC — the setters for ShowFilter and SaveValues
    /// raise a <c>Specified</c> bit that <c>SourceObjectDefinition.Equals</c> compares and
    /// <c>Freeze()</c> clones, so collapsing the two changes what page-customization merge
    /// sees. Measured above: the compiler writes <c>LinksAllowed="1" PopulateAllFields="0"
    /// SaveValues="0" ShowFilter="1"</c> for exactly this AL.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_SourceObjectPropertiesStatedAsTheirAlDefaults_AreStillCarried()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SourceObjectPropsSymbolReference));

            var sourceObject = ReadSourceObjectFor(SodAlDefaultsPageId);

            Assert.Equal("1", sourceObject.GetAttribute("LinksAllowed"));
            Assert.Equal("1", sourceObject.GetAttribute("ShowFilter"));
            Assert.Equal("0", sourceObject.GetAttribute("SaveValues"));
            Assert.Equal("0", sourceObject.GetAttribute("PopulateAllFields"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The negative direction, and the reason the two tests above cannot be satisfied by
    /// writing all five unconditionally: a page whose symbol file states none of them must
    /// carry none of them. Absence is BC's own representation of "the AL declares nothing
    /// here", and the compiler emits exactly that.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_PageStatingNoneOfTheFive_CarriesNoneOfThem()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SourceObjectPropsSymbolReference));

            var sourceObject = ReadSourceObjectFor(SodStatesNonePageId);

            foreach (var name in SodProperties)
                Assert.False(sourceObject.HasAttribute(name),
                    $"a page whose symbol file states no {name} must not be given the attribute");
            // …while everything the file DOES state is still there.
            Assert.Equal("88123620", sourceObject.GetAttribute("SourceTable"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The five do not belong to the SourceTable the way InsertAllowed/AutoSplitKey do: the
    /// compiler writes them on a <c>&lt;SourceObject&gt;</c> that carries no SourceTable
    /// attribute at all (measured above), and 30 Base Application 28.1 pages are that shape —
    /// wizards and NavigatePages declaring <c>LinksAllowed = false</c> / <c>ShowFilter =
    /// false</c>, and page 9991 "Code Coverage Setup" declaring <c>SaveValues = true</c>.
    /// Nesting them inside the <c>SourceTable &gt; 0</c> branch would drop all 30 silently.
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_SourceObjectPropertiesWithoutASourceTable_AreStillCarried()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SourceObjectPropsSymbolReference));

            var sourceObject = ReadSourceObjectFor(SodNoSourceTablePageId);

            Assert.False(sourceObject.HasAttribute("SourceTable"));
            Assert.Equal("0", sourceObject.GetAttribute("LinksAllowed"));
            Assert.Equal("0", sourceObject.GetAttribute("ShowFilter"));
            Assert.Equal("1", sourceObject.GetAttribute("SaveValues"));
            Assert.False(sourceObject.HasAttribute("PopulateAllFields"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// <c>DataCaptionFields</c> is the one of the five that is not a boolean, and the only one
    /// with a shape the runner must check rather than pass through: BC reads it as a
    /// comma-separated list of FIELD NUMBERS. Every one of the 381 Base Application 28.1
    /// pages stating it states numbers, because the same compiler produces both the symbol
    /// file and the metadata — but a value that is not that shape cannot be reconstructed
    /// into one here (resolving names would need the source table's field inventory, which a
    /// page with no source table does not have at all), so it is omitted and SAID, never
    /// written through as text BC would misread as field numbers.
    ///
    /// <para>Omitting is itself a wrong answer — it reads as "this page declares no data
    /// caption fields" — which is exactly why the diagnostic is part of the claim and is
    /// asserted here. It is the same choice, for the same reason, that the SourceTableView
    /// <c>Sorting</c> arm already makes: nothing downstream can be made to fail on this
    /// value, so the failure has to be reported rather than encoded.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_DataCaptionFieldsThatIsNotFieldNumbers_IsRefusedLoudlyNotWrittenAsText()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        var previousError = Console.Error;
        var captured = new StringWriter();
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SourceObjectPropsSymbolReference));

            // This test is the only requester of this page id, and the built document is
            // memoized per id, so the one and only build happens inside the capture.
            Console.SetError(captured);
            var sourceObject = ReadSourceObjectFor(SodBadCaptionFieldsPageId);
            Console.SetError(previousError);

            Assert.False(sourceObject.HasAttribute("DataCaptionFields"),
                "a DataCaptionFields value that is not a field-number list must not be written through");

            var diagnostic = captured.ToString();
            Assert.Contains("DataCaptionFields", diagnostic);
            Assert.Contains(SodBadCaptionFieldsPageId.ToString(), diagnostic);
        }
        finally
        {
            Console.SetError(previousError);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The combination is its own case, and the corpus found it when the unit tests could not:
    /// <c>&lt;SourceTableView&gt;</c> is a CHILD ELEMENT of <c>&lt;SourceObject&gt;</c> while the
    /// #2860 five are ATTRIBUTES of it, and <c>XmlWriter</c> refuses an attribute once the
    /// writer has entered element content. Writing the five after the view therefore threw
    /// <c>InvalidOperationException</c> for exactly the pages declaring BOTH — Base Application
    /// 700 "Error Messages" and 1710 "Deferral Lines - G/L", each <c>LinksAllowed = 0</c>
    /// alongside a <c>SourceTableView</c> — and the throw surfaced as a NULL metadata document,
    /// so BC NRE'd in
    /// <c>NCLMetaForm.GetFrozenPageDefinitionWithExtensionWithoutMergedMultiLanguage</c> and
    /// page 1710's view silently stopped filtering. Three corpus tests caught it against a
    /// clean 2599/2599 baseline.
    ///
    /// <para>So this asserts both halves survive together, which is what pins the write
    /// ORDER — attributes first, child elements after.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_PageWithBothAViewAndSourceObjectProperties_CarriesBoth()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, ViewSymbolReference));

            var sourceObject = ReadSourceObjectFor(ViewPlusPropsPageId);
            var ns = MetaNs(sourceObject.OwnerDocument!);

            Assert.Equal("0", sourceObject.GetAttribute("LinksAllowed"));
            Assert.Equal("1", sourceObject.GetAttribute("PopulateAllFields"));

            var filter = (XmlElement?)sourceObject.SelectSingleNode("m:SourceTableView/m:TableFilters", ns);
            Assert.NotNull(filter);
            Assert.Equal("2", filter!.GetAttribute("FieldID"));
            Assert.Equal("CONST", filter.GetAttribute("FilterType"));
            Assert.Equal("7", filter.GetAttribute("FilterValue"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The boolean counterpart of the <c>DataCaptionFields</c> refusal above, and it is held to
    /// the same standard: a property the symbol file STATES in a form this cannot read as a
    /// boolean must not be folded into "the page declares nothing". Both produce the same
    /// absent attribute, so absence alone cannot tell them apart — which is exactly why the
    /// diagnostic is part of the claim rather than a nicety.
    ///
    /// <para>The report is driven from <c>PageSymbol.UnreadableBooleanProperties</c>, carried in
    /// the parsed PAYLOAD rather than written at parse time, and that is deliberate. Parsing
    /// happens behind a content-addressed on-disk cache, so a <c>Console.Error</c> line written
    /// from the parser is emitted on a cache MISS and silently lost on every warm run after —
    /// the failure mode <c>AlPageMetadataRegistry</c>'s header calls "the trap this whole class
    /// exists to avoid". Putting it in the payload means a cache HIT replays it, which is also
    /// the correct answer: the same bytes carry the same unreadable value.</para>
    ///
    /// <para>The page states a second, readable property too, so this cannot pass by the
    /// synthesizer giving up on the page as a whole.</para>
    /// </summary>
    [Fact]
    public void TryBuildDependencyPageMetadata_BooleanStatedInAFormItCannotRead_IsRefusedLoudlyNotTreatedAsUnstated()
    {
        var dir = TestScratch.Dir("al-runner-dep-pagemeta-xml-tests");
        Directory.CreateDirectory(dir);
        var previousError = Console.Error;
        var captured = new StringWriter();
        try
        {
            RecordPatches.AddBcAppPath(WriteApp(dir, SourceObjectPropsSymbolReference));

            // Only this test asks for this page id, and the document is memoized per id, so
            // the one and only build happens inside the capture.
            Console.SetError(captured);
            var sourceObject = ReadSourceObjectFor(SodUnreadableBoolPageId);
            Console.SetError(previousError);

            Assert.False(sourceObject.HasAttribute("PopulateAllFields"),
                "an unreadable value must not be invented into a readable one");
            // …and the rest of the page is unaffected.
            Assert.Equal("0", sourceObject.GetAttribute("LinksAllowed"));

            var diagnostic = captured.ToString();
            Assert.Contains("PopulateAllFields", diagnostic);
            Assert.Contains(SodUnreadableBoolPageId.ToString(), diagnostic);
            // The unreadable value itself, so the reader can see WHAT the file said.
            Assert.Contains("yes", diagnostic);
            // The readable one must not be reported as a problem.
            Assert.DoesNotContain("LinksAllowed", diagnostic);
        }
        finally
        {
            Console.SetError(previousError);
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