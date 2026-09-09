// RecordPatches.PageControlFieldFromBcDocument — answer Page Control Field (2000000192)
// from BC's own emitted page document instead of re-deriving the control tree from AL
// source text (issue #3604, part of the conversion chain #3562 tracks). Follows
// RecordPatches.ReportRowFromBcDocument.cs (#3607) and
// RecordPatches.NclMetaTableFromBcDocument.cs (#3584).
//
// WHERE THE DOCUMENT COMES FROM
//   AlObjectMetadataRegistry holds the runtime metadata document BC's own Compilation.Emit
//   produced for every object the runner compiled, keyed (kind, id) — "Page" here. The page
//   EXECUTION path already loads it (RecordPatches.RealPageMetadata.cs); this virtual table
//   read none of it.
//
// WHAT BC DOES, AND WHY THE AL DERIVATION DISAGREES WITH IT
//   PageControlFieldDataProvider.GetControlsOnPage (Ncl 28.1) walks the merged master page
//   with `FindAll(ed => ed is ControlDefinition)`, numbering the walk as Sequence, then
//   sorting by control ID. Four consequences, each of which the AL derivation got wrong and
//   each of which corpus codeunit 60426 pins on a concrete value:
//
//     SourceExpression   BC states the source FIELD's Name for a bound control, and the
//                        DataFieldDefinition's SourceExpression for an unbound one. The AL
//                        derivation stated the binding TEXT: `Rec."Entry No."`, not
//                        `Entry No.`.
//     unbound controls   BC lists every field control. The AL derivation dropped any control
//                        not bound as exactly `Rec.Something`, so a control bound to a page
//                        variable had NO ROW AT ALL.
//     OptionString       BC states the source field's option string for an Option-bound
//                        control. The AL derivation always fell through to
//                        GetDefaultNavValue, i.e. ''.
//     TableNo            BC states the page's source table on EVERY row, including one with
//                        no source field.
//
//   docs/page-control-field-from-bc-document.md has the emitted document, the reflection
//   measurements behind the three attribute defaults, and what is deliberately left alone.
//
// WHY THE XML AND NOT THE MERGED MetaPageDefinition EnsureRealPageMetadata BUILDS
//   Written down rather than rediscovered, because it is the same trap #3607 hit one table
//   over. EnsureRealPageMetadata forces a real NCLMetaForm metadata load per page as a
//   deliberate side effect. Populating a virtual table asks about EVERY known page, so that
//   turns one Page Control Field read into a metadata load per page and blows the 60s
//   per-test watchdog. Every value below is stated directly by the document.
//
// SCOPE — EMIT-CAPTURED PAGES ONLY
//   A page from a precompiled dependency .app keeps its SymbolReference.json-derived rows.
//   DependencyPageMetadataXml.EmitPageXml deliberately reconstructs NO <Controls> element
//   for such a page (its own header says so), so routing dependency pages through this file
//   would answer zero rows for every one of them — a regression, not a conversion. The gate
//   is HasBcPageMetadataDocument, mirroring HasBcTableMetadataDocument (#3552).
//
// PRECOMPILED-DLL RESPECT
//   Reads an XML document BC's own emitter produced, and resolves the source field through
//   the same NCLMetaTable the runner already builds. No BC method body is rewritten and no
//   AL business logic is touched.
using System.Xml;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>The AL compiler's own <c>SymbolKind</c> name for a page, as
    /// <see cref="AlObjectMetadataRegistry"/> keys it. Measured from the emitter's own trace
    /// (<c>AL_RUNNER_TRACE_OBJECT_METADATA=2</c> prints <c>Page|id:70002</c>), not assumed
    /// from the AL keyword.</summary>
    internal const string BcPageMetadataKind = "Page";

    /// <summary>
    /// One field control as BC's document states it, before the source field is resolved.
    /// <paramref name="DataColumnName"/> is the whole binding decision: BC's
    /// <c>ControlDefinition.IsBoundToTableField(out fieldNo)</c> is
    /// <c>int.TryParse(DataColumnName, out fieldNo)</c> and nothing else.
    /// </summary>
    private sealed record BcPageControl(
        int ControlId, string ControlName, string DataColumnName,
        string Enabled, string Editable, string Visible, int Sequence);

    /// <summary>The subset of BC's page document this table reads.
    /// <paramref name="Expressions"/> is <c>page.Expressions</c> — the
    /// <c>DataFieldDefinition</c> list BC looks an unbound control's source expression up in,
    /// keyed by the control's <c>DataColumnName</c>.</summary>
    private sealed record BcPageDocument(
        int SourceTableId,
        List<BcPageControl> Controls,
        Dictionary<string, (string SourceExpression, string OptionString, bool IsOption)> Expressions);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, BcPageDocument?>
        _bcPageControlDocuments = new();

    /// <summary>
    /// Drop the parsed documents on a <c>--watch</c>/<c>--server</c> reload. Page ids repeat
    /// across reloads, so an entry parsed from the PREVIOUS bundle's document is a wrong
    /// answer rather than a miss — the same statement <see cref="ClearBcReportDocuments"/>
    /// makes for reports (#3607).
    /// </summary>
    internal static void ClearBcPageControlDocuments() => _bcPageControlDocuments.Clear();

    /// <summary>
    /// True when BC's emitter handed the runner a metadata document for this page id. A page
    /// with none — a precompiled dependency's, or one served from a cache written before the
    /// registry existed — answers false and keeps the AL/symbol derivation.
    /// </summary>
    internal static bool HasBcPageMetadataDocument(int pageId)
        => AlObjectMetadataRegistry.TryGet(BcPageMetadataKind, pageId, out var xml)
           && !string.IsNullOrEmpty(xml);

    private static BcPageDocument? TryGetBcPageControlDocument(int pageId)
        => _bcPageControlDocuments.GetOrAdd(pageId, static id =>
        {
            if (!AlObjectMetadataRegistry.TryGet(BcPageMetadataKind, id, out var xml) || string.IsNullOrEmpty(xml))
                return null;
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                return doc.DocumentElement == null ? null : ParseBcPageDocument(doc.DocumentElement);
            }
            catch (XmlException ex)
            {
                // A malformed document is a capture bug, not a reason to serve nothing: fall
                // back to the AL-derived rows and name the page once so it is diagnosable.
                Console.Error.WriteLine(
                    $"[page-control-field] page {id}: BC's metadata document did not parse "
                    + $"({ex.Message}) — falling back to the AL-derived rows");
                return null;
            }
        });

    private static BcPageDocument ParseBcPageDocument(XmlElement root)
    {
        var controls = new List<BcPageControl>();
        var expressions = new Dictionary<string, (string, string, bool)>(StringComparer.Ordinal);
        int sourceTableId = 0;

        foreach (XmlNode node in root.ChildNodes)
        {
            if (node is not XmlElement e) continue;
            switch (e.Name)
            {
                case "Properties":
                    foreach (XmlNode p in e.ChildNodes)
                        if (p is XmlElement so && so.Name == "SourceObject")
                            sourceTableId = ReadBcAttrInt(so, "SourceTable");
                    break;
                case "Content":
                    // Sequence is the index of the FindAll walk, assigned before BC sorts the
                    // rows by control id — so it is the document's own document order, and a
                    // counter threaded through the walk is the only way to reproduce it.
                    var sequence = 0;
                    CollectBcPageControls(e, controls, ref sequence);
                    break;
                case "Expressions":
                    foreach (XmlNode x in e.ChildNodes)
                    {
                        if (x is not XmlElement expr || expr.Name != "Expression") continue;
                        var name = expr.GetAttribute("Name");
                        if (string.IsNullOrEmpty(name)) continue;
                        expressions[name] = (
                            expr.GetAttribute("SourceExpression"),
                            expr.GetAttribute("OptionString"),
                            expr.GetAttribute("Datatype") == "Option");
                    }
                    break;
            }
        }

        return new BcPageDocument(sourceTableId, controls, expressions);
    }

    /// <summary>
    /// Depth-first over the whole layout, collecting exactly the elements BC's
    /// <c>ed is ControlDefinition</c> predicate selects.
    ///
    /// <para>The predicate is a type test on the document's own <c>xsi:type</c>, and it is
    /// exact rather than a prefix match: <c>ControlDefinition</c> and
    /// <c>MetaControlDefinition</c> each have NO subtypes in
    /// <c>Microsoft.Dynamics.Nav.Types</c> 28.x (measured by reflection —
    /// docs/page-control-field-from-bc-document.md#control-definition-has-no-subtypes), so
    /// naming the two type strings selects precisely the field controls. A group
    /// (<c>ControlGroupDefinition</c>), a repeater, a part
    /// (<c>InfopartSystemDefinition</c>) and every action are all excluded by the same
    /// test, which is why nothing here filters on "is this bound to a table field".</para>
    /// </summary>
    private static void CollectBcPageControls(XmlElement parent, List<BcPageControl> into, ref int sequence)
    {
        foreach (XmlNode node in parent.ChildNodes)
        {
            if (node is not XmlElement e) continue;
            // Containers and Controls are both walked; anything else on the page (actions,
            // action containers) can hold no ControlDefinition and is skipped whole.
            if (e.Name != "Containers" && e.Name != "Controls") continue;

            var type = e.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance");
            if (e.Name == "Controls" && (type == "ControlDefinition" || type == "MetaControlDefinition"))
            {
                into.Add(new BcPageControl(
                    ControlId: ReadBcAttrInt(e, "ID"),
                    ControlName: e.GetAttribute("Name"),
                    DataColumnName: e.GetAttribute("DataColumnName"),
                    // Enabled and Visible carry [DefaultValue("true")] on
                    // ControlDefinition, so BC's deserializer supplies "true" when the
                    // attribute is absent. Editable carries NO DefaultValue and defaults to
                    // null, which BC renders as the empty string through
                    // NavText.CreateTruncated — so absent means "" here, not "true". Measured
                    // by reflection over Microsoft.Dynamics.Nav.Types 28.1; see
                    // docs/page-control-field-from-bc-document.md#the-three-property-defaults.
                    Enabled: ReadBcAttrOrDefault(e, "Enabled", "true"),
                    Editable: e.GetAttribute("Editable"),
                    Visible: ReadBcAttrOrDefault(e, "Visible", "true"),
                    Sequence: sequence++));
            }

            CollectBcPageControls(e, into, ref sequence);
        }
    }

    private static int ReadBcAttrInt(XmlElement e, string name)
        => int.TryParse(e.GetAttribute(name), out var v) ? v : 0;

    private static string ReadBcAttrOrDefault(XmlElement e, string name, string fallback)
        => e.HasAttribute(name) ? e.GetAttribute(name) : fallback;

    /// <summary>
    /// The Page Control Field rows for <paramref name="pageId"/>, built from BC's document.
    /// Never called unless <see cref="HasBcPageMetadataDocument"/> is true; returns null when
    /// the document nonetheless did not parse, so the caller keeps the AL derivation.
    ///
    /// <para>Mirrors <c>GetControlsOnPage</c> value for value: <c>TableNo</c> is the page's
    /// source table on every row (BC assigns <c>array[4]</c> from <c>tableNo</c> outside the
    /// bound/unbound branch); <c>FieldNo</c> is the <c>out</c> of the <c>int.TryParse</c>, so
    /// 0 for an unbound control; and SourceExpression/OptionString take the bound branch's
    /// field-derived values or the unbound branch's expression-derived ones.</para>
    /// </summary>
    private static List<PageControlFieldRow>? GetPageControlFieldRowsFromBcDocument(int pageId)
    {
        var doc = TryGetBcPageControlDocument(pageId);
        if (doc == null) return null;

        var rows = new List<PageControlFieldRow>(doc.Controls.Count);
        foreach (var c in doc.Controls)
        {
            var isBound = int.TryParse(c.DataColumnName, out var fieldNo);
            var sourceExpression = string.Empty;
            var optionString = string.Empty;

            if (isBound)
            {
                // BC: `if (tableNo > 0) { metaTable ??= GetTableMetadata(tableNo);
                //      var f = metaTable.GetFieldsById(fieldNo); if (f != null) { … } }`.
                // A field the metatable does not carry leaves both values empty rather than
                // being guessed at, exactly as BC's null check does.
                if (doc.SourceTableId > 0)
                {
                    var field = FindMetaFieldById(doc.SourceTableId, fieldNo);
                    if (field != null)
                    {
                        sourceExpression = field.FieldName ?? string.Empty;
                        // BC gates this on `f.Type == NavType.Option`, so the guard is the
                        // field's NavType and not "does the field carry option metadata".
                        // The two happen to agree here, and the reason is worth stating
                        // because it looks like a bug: an AL `Enum "X"` field is
                        // NavType.Option as well. BC's own emitted table document writes
                        // Datatype="Option" for an Enum field and distinguishes it only by
                        // an EnumTypeId attribute (measured — see
                        // docs/page-control-field-from-bc-document.md#option-and-enum), and
                        // the runner stores it the same way for a load-bearing reason of its
                        // own: BC's generated AL calls ValidateExpectedType(fieldNo,
                        // NavType.Option) when reading an enum-typed record field
                        // (RecordPatches.NclMetaTableBuilder.cs). So an Enum-bound control
                        // takes this branch too, which is what a real tier is expected to do
                        // and is NOT pinned by any corpus test today — #3625 tracks it.
                        optionString = field.FieldNavType == Microsoft.Dynamics.Nav.Types.NavType.Option
                            ? field.FieldOptionMetadata?.OptionString ?? string.Empty
                            : string.Empty;
                    }
                }
            }
            else if (doc.Expressions.TryGetValue(c.DataColumnName, out var expr))
            {
                sourceExpression = expr.SourceExpression ?? string.Empty;
                if (expr.IsOption) optionString = expr.OptionString ?? string.Empty;
            }

            rows.Add(new PageControlFieldRow(
                pageId, c.ControlId, c.ControlName,
                doc.SourceTableId, isBound ? fieldNo : 0,
                c.Enabled, c.Editable, c.Visible,
                sourceExpression, optionString, c.Sequence));
        }

        return rows;
    }

    /// <summary>
    /// The source field BC would resolve through
    /// <c>MetadataProvider.GetTableMetadata(tableNo).GetFieldsById(fieldNo)</c>. Goes through
    /// the runner's own NCLMetaTable rather than the parsed AL table, because that is the
    /// object that carries tableextension-added fields and the resolved option metadata for an
    /// Enum-typed field; the parsed table carries neither.
    /// </summary>
    private static Microsoft.Dynamics.Nav.Runtime.NCLMetaField? FindMetaFieldById(int tableId, int fieldNo)
    {
        var meta = GetOrBuildNCLMetaTable(tableId);
        if (meta == null) return null;
        foreach (var f in GetAllFields(meta) ?? Enumerable.Empty<Microsoft.Dynamics.Nav.Runtime.NCLMetaField>())
            if (f.FieldNo == fieldNo) return f;
        return null;
    }
}
