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
            }
          ]
        }
        """;

    [Fact]
    public void TryBuildDependencyPageMetadata_KnownPage_ProducesPageDefinitionWithRealPageTypeAndSourceTable()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
}
