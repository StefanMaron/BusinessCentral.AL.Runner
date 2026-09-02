// RecordPatches.DependencyPageMetadataXml — runtime PageDefinition XML for pages that live
// in a PRECOMPILED dependency .app, which the runner never source-compiles.
//
// THE GAP (issue #1939)
//   NavForm.GetMasterPage() -> NavGlobal.MetadataProvider.GetMasterPage(...) ->
//   GetMergedMasterPage() -> GetPageDefinition(id) is BC's only route to a page's real
//   PageProperties (PageType, SourceObject, ...). NavTestExecution.FindPageType reads
//   exactly one of those — form.MasterPage.PageProperties.PageType — to decide whether a
//   modal page's [ModalPageHandler] is a FilterPage/RequestPage/ModalPage handler, BEFORE
//   the handler ever runs. RunnerFormInit.ShouldResolveMasterPage only let that real lookup
//   run for a page the runner captured emit-time metadata XML for (AlPageMetadataRegistry —
//   populated only by BcCompiler.Emit, which never runs for a page shipped compiled inside a
//   dependency .app). Every other page got GetMasterPage() short-circuited to null, and
//   FindPageType NRE'd on the null MasterPage before the handler dispatch it exists to gate.
//
//   Same root cause, same fix shape, as DependencyReportMetadata.cs one file up: an R2R
//   .app ships no compiled metadata form of its objects (that only exists at real-BC
//   PUBLISH time, which the runner never performs), so the runner reconstructs a runtime
//   metadata document from what the .app DOES ship.
//
// WHAT IS RECONSTRUCTED, AND FROM WHAT
//   SymbolReference.json alone (via BcAppSymbolCache.PageSymbol) — the same typed slice
//   #1769/#1779 already parse for the Page Metadata virtual table. Nothing here is inferred
//   from behaviour or defaulted to something convenient: Id / Name / PageType / Caption /
//   Editable / SourceObject (SourceTable + SourceTableTemporary) come straight off the
//   symbol file's own Properties array.
//
// WHAT IS DELIBERATELY OMITTED, AND WHY THAT IS SAFE HERE
//   Content/Controls, ActionContainers, ViewContainers, AnalysisViewContainers — the page's
//   full control tree and action ribbon. Two independent reasons neither is needed for the
//   gap this file closes:
//     1. NavTestExecution.FindPageType — the NRE site — reads exactly one property,
//        form.MasterPage.PageProperties.PageType, which Properties above already states.
//     2. A precompiled page's control -> value BINDINGS are not read from this XML at all.
//        They come from the page's OWN CallInitializeComponentExtensionMethod /
//        RegisterSourceExpression IL inside the .app's DLL, gated by
//        RunnerFormInit.ShouldRunRealFormInit — a NARROWER, per-instance opt-in that only
//        the runner's own TestPage construction path sets (RunnerPageInstance.MarkRealInit).
//        A page AL opens itself via `SomePage.RunModal()` — this issue's shape — is never
//        marked, so that IL stays no-op'd exactly as it already was; this file's XML cannot
//        change that. Reconstructing a control tree from SymbolReference.json (whose
//        SourceExpression is AL text, e.g. `Rec."No."`, not the compiled field-number
//        DataColumnName the real XML carries) without a way to exercise it would only add
//        guessed data with no path to prove it faithful — the loud-failures rule's
//        anti-pattern. Field-level TestPage control resolution for a page the runner did not
//        compile itself stays out of scope (RunnerPageInstance.cs already documents this),
//        unchanged by this fix — a modal page whose handler drives a field it does not
//        recognise still refuses loudly, exactly as it did before.
using System.Text;
using System.Xml;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string?> _depPageMetadataXml = new();

    /// <summary>
    /// Whether some loaded dependency .app's SymbolReference.json describes
    /// <paramref name="pageId"/> — the opt-in condition <see cref="Patches.RunnerFormInit"/>
    /// and <see cref="EnsureRealPageMetadata"/> widen for, alongside the runner's own
    /// AlPageMetadataRegistry entries.
    /// </summary>
    internal static bool HasDependencyPageMetadata(int pageId) => TryGetDependencyPageSymbol(pageId) != null;

    /// <summary>
    /// Runtime PageDefinition metadata XML for a page declared by a precompiled dependency,
    /// or null when no loaded dependency .app describes that page. Result cached per id
    /// (including the null), since the answer is a property of the loaded dependency set.
    /// </summary>
    internal static string? TryBuildDependencyPageMetadata(int pageId)
        => _depPageMetadataXml.GetOrAdd(pageId, BuildDependencyPageMetadata);

    private static string? BuildDependencyPageMetadata(int pageId)
    {
        var page = TryGetDependencyPageSymbol(pageId);
        if (page == null) return null;

        var xml = EmitPageXml(page);
        Console.Error.WriteLine(
            $"[RecordPatches] dependency page metadata: synthesized Page {pageId} \"{page.Name}\" "
            + $"(PageType={page.PageType}, SourceTable={page.SourceTableId})");
        return xml;
    }

    private static string EmitPageXml(BcAppSymbolCache.PageSymbol page)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, settings))
        {
            w.WriteStartElement("PageDefinition", "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects");
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("MetadataVersion", "130000");
            w.WriteAttributeString("ID", page.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteAttributeString("Name", page.Name);
            w.WriteAttributeString("ALNamespace", string.Empty);

            w.WriteStartElement("Properties");
            w.WriteAttributeString("SourceExtensionType", "ModernDev");
            w.WriteAttributeString("PageType", page.PageType);
            w.WriteAttributeString("Editable", page.Editable ? "1" : "0");
            w.WriteAttributeString("Extensible", "1");
            if (!string.IsNullOrEmpty(page.Caption))
            {
                w.WriteStartElement("CaptionML");
                w.WriteStartElement("Caption");
                w.WriteAttributeString("Id", "1033");
                w.WriteString(page.Caption);
                w.WriteEndElement();
                w.WriteEndElement();
            }
            // ALWAYS written, even for a page with no source table (issue #2451). Same
            // reason as the empty <Content> element below: MetaPageDefinition deserializes a
            // MISSING element to null rather than to an empty one, and BC dereferences this
            // one WITHOUT a null check —
            // MetadataProvider.MergePageAndTable reads
            // `masterPage.PageProperties.SourceObject.SourceTable > 0` as its first act.
            // Omitting it NREs inside BC's own metadata merge, which
            // RunnerPageInstance.TryCreateRecordless catches and turns into null, which
            // silently demotes the TestPage to the navigation mock — every action there
            // answers Enabled = true and Invoke() is a literal no-op.
            //
            // The real AL compiler writes it unconditionally too: across the 3187 page
            // metadata documents in this machine's dependency-compile sidecars,
            // <SourceObject> appears in all 3187, and in 1114 of them it carries no
            // SourceTable attribute at all — the empty form written here.
            w.WriteStartElement("SourceObject");
            if (page.SourceTableId > 0)
            {
                w.WriteAttributeString("SourceTable",
                    page.SourceTableId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (page.SourceTableTemporary)
                    w.WriteAttributeString("SourceTableTemporary", "1");
                if (!page.InsertAllowed) w.WriteAttributeString("InsertAllowed", "0");
                if (!page.ModifyAllowed) w.WriteAttributeString("ModifyAllowed", "0");
                if (!page.DeleteAllowed) w.WriteAttributeString("DeleteAllowed", "0");
            }
            // The attributes above only mean anything alongside a SourceTable, so a page
            // without one gets the bare element the compiler itself emits — not
            // SourceTable="0", which would answer "table 0" to a question about a table the
            // page does not have.
            w.WriteEndElement(); // SourceObject
            w.WriteEndElement(); // Properties

            // Present-but-empty for the third time, and for the third identical reason:
            // MetadataProvider.LoadExpressionRelationTables iterates
            // `masterPage.Expressions` with no null check, so a missing element NREs one
            // statement after the SourceObject read above. The real compiler emits it on all
            // 3187 documents measured. No expressions are reconstructed here — the
            // synthesizer carries no control tree (see the file header) — so this
            // deserializes to an empty collection, which is what a page with no bound
            // controls would have anyway.
            w.WriteStartElement("Expressions");
            w.WriteEndElement();

            // An empty (but present) Content element, not an absent one: NCLMetaForm.
            // LoadPageMetadata()'s own post-load check (EnsureNoControlIdAppearsMoreThanOnce)
            // unconditionally iterates page.Content.Containers, and MetaPageDefinition
            // deserializes a MISSING <Content> element to a null Content rather than an
            // empty one — so leaving the element out entirely NREs there, one call deeper
            // than the FindPageType gap this file exists to close. No control tree is
            // reconstructed (see the file header), so this iterates zero containers.
            w.WriteStartElement("Content");
            w.WriteEndElement();

            w.WriteEndElement(); // PageDefinition
        }
        return sb.ToString();
    }
}
