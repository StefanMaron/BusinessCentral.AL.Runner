// RecordPatches.TableExtensionFieldDeltas — apply a tableextension's `modify(<field>)`
// property changes, read from BC's OWN emitted delta document (issue #3614).
//
// WHY BC'S DOCUMENT AND NOT THE AL SOURCE
//   The AL parse discards `modify(...)` at the syntax level: TryParseTableExtensionFile
//   filters the extension's field list with OfType<FieldSyntax>(), and a modify block parses
//   as FieldModificationSyntax. Re-deriving the property list from that syntax would mean
//   re-implementing AL's rules for which properties a modify block may even set — and those
//   rules are narrow and non-obvious (see the table in
//   docs/object-metadata-from-bc.md#modify-deltas). BC's compiler has already applied them and
//   emitted the answer, which AlObjectMetadataRegistry has been capturing since #3548:
//
//     <MetadataRuntimeDeltas ID="70901" Name="Probe Ext">
//       <FieldChange TargetID="2" TargetType="MetaField" CaptionML="ENU=Modified Description" />
//     </MetadataRuntimeDeltas>
//
//   So this reads the compiler's conclusion rather than recomputing it. Measured on BC 28.1;
//   the probe and the full per-property acceptance table are in
//   docs/object-metadata-from-bc.md#modify-deltas.
//
// WHY THE BASE TABLE'S OWN DOCUMENT IS NOT ENOUGH
//   BC's compiler folds an extension's ADDED fields into the base table's document (a
//   same-app add-only extension therefore needs nothing from here, which is what #3600's
//   relaxed guard rests on) but leaves MODIFIED properties in the extension's own delta.
//   Both halves were measured on the same probe: the base document carried the added
//   `<Field Name="Extra" ID="50">` while its `Description` field still read
//   CaptionML="ENU=Original Caption".
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>The <see cref="AlObjectMetadataRegistry"/> kind a tableextension's delta
    /// document is stored under. BC's emitter reports the AL SymbolKind, so this is
    /// "TableExtension" even though the document's root element is
    /// <c>MetadataRuntimeDeltas</c> — see docs/object-metadata-capture.md#which-kinds-arrive,
    /// which records that mismatch as the thing that put a non-existent kind into #3562.</summary>
    internal const string BcTableExtensionMetadataKind = "TableExtension";

    /// <summary>
    /// One field-property change a <c>modify(...)</c> block declared, as BC's delta states it.
    /// Only properties the derivation can actually carry onto an AL-observable surface are
    /// modelled; <see cref="ApplyTableExtensionFieldDeltas"/> reports anything else loudly
    /// rather than dropping it, because a silently discarded delta is the defect #3614 is.
    /// </summary>
    /// <param name="TargetFieldId">The base-table field id the change applies to.</param>
    /// <param name="Caption">The new caption, already unwrapped from CaptionML's
    /// <c>ENU=</c> form, or null when the delta changes no caption.</param>
    internal readonly record struct BcFieldChange(int TargetFieldId, string? Caption);

    /// <summary>
    /// Every <c>&lt;FieldChange&gt;</c> BC emitted for tableextension <paramref name="extensionId"/>,
    /// or an empty list when it emitted no delta document (the ordinary case: an extension that
    /// only ADDS fields produces no FieldChange at all).
    ///
    /// <para>Parse failures are LOUD and return nothing rather than a partial list. A delta
    /// this cannot read is a property change that will not be applied, which is exactly the
    /// silent discard #3614 reports — so it says so on stderr instead of shrinking quietly.
    /// The document is the compiler's own output, so a malformed one is a runner-side shape
    /// gap worth seeing, not a user error to tolerate.</para>
    /// </summary>
    internal static IReadOnlyList<BcFieldChange> ReadBcFieldChanges(int extensionId)
    {
        if (!AlObjectMetadataRegistry.TryGet(BcTableExtensionMetadataKind, extensionId, out var xml)
            || string.IsNullOrEmpty(xml))
            return Array.Empty<BcFieldChange>();

        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var changes = new List<BcFieldChange>();
            foreach (var node in doc.GetElementsByTagName("FieldChange").OfType<XmlElement>())
            {
                // TargetType discriminates what the change is about. A tableextension's delta
                // can carry entries for things other than a field property (BC uses the same
                // MetadataRuntimeDeltas envelope for several extension kinds), so a non-field
                // entry is skipped ON PURPOSE and not counted as a failure.
                var targetType = node.GetAttribute("TargetType");
                if (!string.Equals(targetType, "MetaField", StringComparison.Ordinal)) continue;

                // A FieldChange with no readable TargetID cannot be applied to anything. That
                // is a real discard, so it is reported rather than skipped — the diagnostic
                // sits ABOVE the `continue`, which is the shape #3609 found dropped four
                // times over.
                if (!int.TryParse(node.GetAttribute("TargetID"), out var targetId))
                {
                    Console.Error.WriteLine(
                        $"[TableExtDelta] tableextension {extensionId}: a <FieldChange> carries no readable "
                        + $"TargetID (got '{node.GetAttribute("TargetID")}'); its property changes are NOT applied");
                    continue;
                }

                // CaptionML is BC's multi-language form, `ENU=<text>`. The derivation's
                // ParsedField.Caption is the plain text, the same shape CaptionFrom produces
                // from AL source, so unwrap to match. An absent attribute means this delta
                // changes no caption — an empty FieldChange is normal and NOT a failure: a
                // design-time-only property (Description) emits exactly that.
                string? caption = null;
                var captionMl = node.GetAttribute("CaptionML");
                if (!string.IsNullOrEmpty(captionMl))
                    caption = UnwrapEnuCaption(captionMl);

                if (caption != null)
                    changes.Add(new BcFieldChange(targetId, caption));

                ReportUnappliedAttributes(extensionId, targetId, node);
            }
            return changes;
        }
        catch (XmlException ex)
        {
            Console.Error.WriteLine(
                $"[TableExtDelta] tableextension {extensionId}: its metadata delta document could not be "
                + $"parsed ({ex.GetType().Name}: {ex.Message}); every modify(...) property change it "
                + "declares is NOT applied");
            return Array.Empty<BcFieldChange>();
        }
    }

    /// <summary>
    /// Attributes on a <c>&lt;FieldChange&gt;</c> that carry no property change to apply:
    /// the two that identify the entry, and the ones BC puts on every element.
    /// </summary>
    private static readonly HashSet<string> FieldChangeBookkeepingAttributes =
        new(StringComparer.Ordinal) { "TargetID", "TargetType", "xmlns" };

    /// <summary>
    /// Report every property this reader did NOT apply, naming the property and the field.
    ///
    /// <para>Seven properties are legal in a table field's <c>modify(...)</c> and only
    /// <c>Caption</c> has an AL-readable surface on the derivation today
    /// (docs/object-metadata-from-bc.md#modify-accepts). Staying quiet about the other six
    /// would rebuild the exact defect #3614 reports one level down: a property change BC
    /// stated, dropped with nothing said. So the unhandled ones are named rather than
    /// ignored, and the next one to acquire a surface shows up as a diagnostic instead of as
    /// a wrong value.</para>
    ///
    /// <para>Attribute-driven rather than a list of known-unhandled names: a property BC adds
    /// later is reported the day it appears, with no edit here.</para>
    /// </summary>
    private static void ReportUnappliedAttributes(int extensionId, int targetFieldId, XmlElement node)
    {
        foreach (var attribute in node.Attributes.OfType<XmlAttribute>())
        {
            var name = attribute.LocalName;
            if (FieldChangeBookkeepingAttributes.Contains(name)) continue;
            if (name.StartsWith("xmlns", StringComparison.Ordinal)) continue;
            // The one this reader does apply.
            if (string.Equals(name, "CaptionML", StringComparison.Ordinal)) continue;

            Console.Error.WriteLine(
                $"[TableExtDelta] tableextension {extensionId}: modify(...) on field {targetFieldId} "
                + $"declares {name}='{attribute.Value}', which this runner does not apply — the field "
                + "keeps its original value. See docs/object-metadata-from-bc.md#modify-accepts");
        }
    }

    /// <summary>
    /// The text of a BC <c>CaptionML</c>/<c>ToolTipML</c> attribute for the ENU language, or
    /// null when it states none. The value is a semicolon-separated list of
    /// <c>&lt;lang&gt;=&lt;text&gt;</c> pairs; the runner is single-language and every other
    /// caption it handles is the ENU one, so this reads ENU and ignores the rest.
    /// </summary>
    private static string? UnwrapEnuCaption(string captionMl)
    {
        foreach (var part in captionMl.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(part.Substring(0, eq).Trim(), "ENU", StringComparison.OrdinalIgnoreCase))
                return part.Substring(eq + 1);
        }
        return null;
    }

    /// <summary>
    /// Return <paramref name="fields"/> with every <c>modify(...)</c> property change declared
    /// by any tableextension on <paramref name="baseTableName"/> applied, reading the changes
    /// from BC's own delta documents.
    ///
    /// <para>Returns the input unchanged when no extension declares one, which is the common
    /// case — so the ordinary path allocates nothing.</para>
    ///
    /// <para><b>A change naming a field that is not there is reported, never dropped.</b> BC's
    /// compiler will not emit a FieldChange for a field that does not exist, so if one arrives
    /// for an id this table has no field for, the runner's own field list disagrees with the
    /// compiler's — a shape gap worth a line on stderr rather than a silent no-op.</para>
    /// </summary>
    internal static IReadOnlyList<ParsedField> ApplyTableExtensionFieldDeltas(
        string baseTableName, IReadOnlyList<ParsedField> fields)
    {
        if (string.IsNullOrEmpty(baseTableName) || fields.Count == 0) return fields;
        if (!_extensionIdsByBaseTable.TryGetValue(baseTableName.ToLowerInvariant(), out var extIds)
            || extIds.Count == 0)
            return fields;

        // Later extensions win on a contested field id, matching the order BC applies deltas
        // in — the registry is keyed per extension, and _extensionIdsByBaseTable preserves
        // merge order.
        Dictionary<int, BcFieldChange>? byFieldId = null;
        foreach (var extId in extIds)
            foreach (var change in ReadBcFieldChanges(extId))
                (byFieldId ??= new Dictionary<int, BcFieldChange>())[change.TargetFieldId] = change;

        if (byFieldId == null || byFieldId.Count == 0) return fields;

        var known = new HashSet<int>(fields.Select(f => f.FieldId));
        foreach (var fieldId in byFieldId.Keys)
            if (!known.Contains(fieldId))
                Console.Error.WriteLine(
                    $"[TableExtDelta] table '{baseTableName}': a modify(...) delta targets field {fieldId}, "
                    + "which this table has no field for; its property changes are NOT applied");

        var result = new List<ParsedField>(fields.Count);
        foreach (var f in fields)
            result.Add(byFieldId.TryGetValue(f.FieldId, out var change) && change.Caption != null
                ? f with { Caption = change.Caption }
                : f);
        return result;
    }

    /// <summary>
    /// Drop every cached NCLMetaTable whose <c>modify(...)</c> deltas were not available when it
    /// was built, so the next lookup rebuilds it with them applied.
    ///
    /// <para><b>Why an eviction and not an in-place poke.</b> A field's caption is a constructor
    /// argument to <c>MetaField</c>, and BC answers <c>FieldCaption</c> from the ORIGINAL
    /// <c>MetaTable</c> the NCLMetaTable was built from (<c>NCLMetaField.CreateCaptionStrings</c>
    /// → <c>GetMetaTableOriginal</c>). There is no setter to poke: applying a caption change
    /// means building the MetaField again, which means building the table again.</para>
    ///
    /// <para><b>Why it has to run post-emit at all.</b> <c>BuildNCLMetaTable</c> first runs during
    /// AddSourceDir and <c>Emit</c> — which registers the delta documents — runs after it, so the
    /// cold build sees an empty registry. Measured on the probe fixture: table 70900 was built
    /// three log lines BEFORE its extension's delta document was registered. This is the same
    /// ordering <see cref="RebuildTablesFromBcMetadataAll"/> and
    /// <c>FixupEnumFieldOptionMetadataAll</c> exist for, and it is called from the same
    /// post-emit point in <c>BcRuntime.SetTestAssembly</c>.</para>
    ///
    /// <para>Eviction goes through <see cref="EvictCachedMetaTableForBaseTable"/> so this
    /// inherits its two companion purges — the field-trigger wiring set (#2463) and the injected
    /// event subscribers (#2510) — rather than reproducing a rebuild that silently loses
    /// them.</para>
    ///
    /// <para>Only tables that actually have a delta are evicted. A bundle with no
    /// <c>modify(...)</c> anywhere evicts nothing and rebuilds nothing.</para>
    /// </summary>
    public static void ApplyTableExtensionFieldDeltasAll()
    {
        // Snapshot first: EvictCachedMetaTableForBaseTable mutates _metaTableCache.
        var toEvict = new List<string>();
        foreach (var kvp in _metaTableCache)
        {
            if (kvp.Value is not NCLMetaTable) continue;
            if (!_parsedTables.TryGetValue(kvp.Key, out var parsed)) continue;
            if (toEvict.Contains(parsed.TableName)) continue;

            // ReferenceEquals: ApplyTableExtensionFieldDeltas returns the SAME list when it
            // changed nothing, so this asks "did any delta apply" without comparing captions
            // field by field.
            if (!ReferenceEquals(
                    ApplyTableExtensionFieldDeltas(parsed.TableName, parsed.Fields), parsed.Fields))
                toEvict.Add(parsed.TableName);
        }

        foreach (var tableName in toEvict)
            EvictCachedMetaTableForBaseTable(tableName);
    }
}
