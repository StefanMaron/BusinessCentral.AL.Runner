// RecordPatches.PermissionSetFromBcDocument — a source-compiled permission set's declaration
// read from BC's OWN emitted <PermissionSet> metadata document instead of re-derived by regex
// over AL source text (#3609, last conversion of the chain #3562 tracks).
//
// Derivation, the probe output, the mask table and the per-symbol survival list are in
// docs/permission-set-from-bc-document.md. Four claims, each with what settled it:
//
// CLAIM — observably equivalent, and strictly better on one column. BC's document states the
//   same properties the regex derived, with object references and masks ALREADY RESOLVED
//   (`<Permission Type="0" ID="70700" Value="15" />`, `IncludedPermissionSets="70701"`). The
//   mask encoding is BC's own and agrees with the derivation's hand-rolled table exactly —
//   RIMD=15, Rimd=449, X=16, measured on BC 28.1. So this is the same answer without the
//   transcription. See docs/permission-set-from-bc-document.md#mask-encoding.
//
// CLAIM — it removes a silent wrong answer, which is why it is preferred unconditionally when
//   available rather than as a tidiness matter. ResolveSourcePermissionEntries resolves an
//   object NAME against ParsedObjectDecls, which carries no tables, so every `tabledata` grant
//   in a source-compiled permission set was dropped by a `continue` sitting ABOVE its own
//   diagnostic — invisible at every verbosity. Measured 17419 → 17421 declared permissions on a
//   probe. See docs/permission-set-from-bc-document.md#tabledata.
//
// CLAIM — ExcludedPermissionSets IS carried here, contrary to what #3609 expected. The issue's
//   0-of-258 count over Base Application reproduces exactly, but it measures Base Application's
//   AL rather than BC's emitter: a probe declaring the property gets it back on the document.
//   See docs/permission-set-from-bc-document.md#excluded-permission-sets.
//
// CLAIM — an ABSENT Assignable attribute reads as FALSE, not as AL's source-language default
//   of true (#2417, #3806). BC's own Types.Metadata.MetaPermissionSet.Create assigns the
//   property only inside `case 10: if (name == "Assignable")`, and MetaPermissionSet()
//   initialises only Permissions/Included/Excluded — so an absent attribute leaves
//   default(bool). Read off Microsoft.Dynamics.Nav.Types.dll 28.1.49838.53910 (sha256
//   c91ede8f…); #3806 measured that same reader answering False for PermissionSet 68, the one
//   document of 178 that omits the attribute. AL's default governs what the COMPILER emits,
//   which is a different question from what BC's reader does with a document stating nothing.
//
// TRAP — nothing in the derivation became dead code, so do not delete it as unreachable. The
//   registry is keyed by id (the parser is the inventory), BC's document does not state
//   AppId/AppName, and Emit runs only on a compile-cache MISS, leaving the derivation the live
//   fallback on a warm run. See docs/permission-set-from-bc-document.md#what-survives.
//
// A parse failure is never re-routed to the derivation — a weaker answer substituted on error
// is wrong metadata under a green build (.claude/rules/loud-failures.md). Every drop inside
// this file is reported on AL_RUNNER_DIAG_PERMMETA, because a silently dropped grant is the
// exact defect #3609 was about.
//
// PRECOMPILED-DLL RESPECT: reads an XML string the compiler already produced and builds the
// runner's own PermissionSetSymbol record from it. No BC type is constructed or rewritten.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>The AL compiler's own <c>SymbolKind</c> spelling for a permission set.
    /// <c>PermissionSetExtension</c> is a DISTINCT kind with its own documents (root element
    /// <c>MetadataRuntimeDeltas</c>, not <c>PermissionSet</c>) and is not read here.</summary>
    internal const string BcPermissionSetMetadataKind = "PermissionSet";

    /// <summary>
    /// True when BC's emitter handed the runner a metadata document for this permission set
    /// id — i.e. the runner compiled it from source this run, or a replay mechanism restored
    /// the document on a compile-cache hit.
    /// </summary>
    internal static bool HasBcPermissionSetDocument(int permissionSetId)
        => AlObjectMetadataRegistry.TryGet(BcPermissionSetMetadataKind, permissionSetId, out var xml)
           && !string.IsNullOrEmpty(xml);

    /// <summary>
    /// The permission set BC's document states, or null when no document is registered for
    /// that id. <paramref name="fallbackName"/> is used only when the document states no
    /// Name, which no shape the compiler produces does — it keeps a nameless row from
    /// becoming an unkeyed one rather than papering over a real gap.
    /// </summary>
    internal static BcAppSymbolCache.PermissionSetSymbol? TryReadPermissionSetFromBcDocument(
        int permissionSetId, string fallbackName)
    {
        if (!AlObjectMetadataRegistry.TryGet(BcPermissionSetMetadataKind, permissionSetId, out var xml)
            || string.IsNullOrEmpty(xml))
            return null;

        XElement root;
        try
        {
            root = XDocument.Parse(xml).Root
                   ?? throw new InvalidOperationException("document has no root element");
        }
        catch (Exception ex)
        {
            // Loud, not re-routed: a document that arrived and will not parse is a shape
            // change in BC's emitter, and answering from the regex derivation instead would
            // hide it behind a green run.
            throw PermissionMetadataBcShapeGap(
                $"PermissionSet {permissionSetId} metadata document",
                $"was registered but does not parse as XML ({ex.Message}) — BC's own permission "
                + "set declaration cannot be read");
        }

        var name = (string?)root.Attribute("Name");
        if (string.IsNullOrEmpty(name)) name = fallbackName;

        return new BcAppSymbolCache.PermissionSetSymbol(
            permissionSetId,
            name,
            // CaptionML is BC's multi-language form, "ENU=PP base caption". The Caption the
            // rest of the runner carries is the ENU text alone, which is what the AL `Caption`
            // property stated and what "Metadata Permission Set".Name reports.
            EnuFromCaptionMl((string?)root.Attribute("CaptionML")),
            // Assignable is emitted as "1"/"0", and an ABSENT attribute is false — the same
            // answer BC's own reader gives this document (see the CLAIM in the header).
            ParseBcBool((string?)root.Attribute("Assignable"), defaultValue: false),
            ReadPermissions(root),
            // The NAME list stays null on this route: BC states ids, and filling both would
            // give BuildIncludeList two sources to disagree about.
            IncludedPermissionSets: null,
            // Access is absent when the set declares none; AL's default is Public, applied by
            // the populator rather than invented here, so absent stays null.
            Access: (string?)root.Attribute("Access"),
            IncludedPermissionSetIds: ReadPermissionSetIdList(root, "IncludedPermissionSets"),
            ExcludedPermissionSetIds: ReadPermissionSetIdList(root, "ExcludedPermissionSets"));
    }

    /// <summary>
    /// The ENU text of a <c>CaptionML</c> value. BC emits "ENU=Some caption", and may emit
    /// further languages separated by ';' — take ENU when present, the first entry otherwise,
    /// so a set captioned only in another language still reports a caption rather than the raw
    /// multi-language string.
    /// </summary>
    private static string? EnuFromCaptionMl(string? captionMl)
    {
        if (string.IsNullOrWhiteSpace(captionMl)) return null;
        string? first = null;
        foreach (var part in captionMl.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var lang = part.Substring(0, eq).Trim();
            var text = part.Substring(eq + 1);
            first ??= text;
            if (string.Equals(lang, "ENU", StringComparison.OrdinalIgnoreCase)) return text;
        }
        return first ?? captionMl;
    }

    private static bool ParseBcBool(string? value, bool defaultValue)
        => string.IsNullOrEmpty(value)
            ? defaultValue
            : value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The document's <c>&lt;Permissions&gt;</c> rows. Both Type and ID are already BC's own
    /// ordinal and object id, and Value is the computed mask — nothing is resolved or
    /// recomputed here, which is the point of reading the document at all.
    /// </summary>
    private static IReadOnlyList<BcAppSymbolCache.PermissionSymbol> ReadPermissions(XElement root)
    {
        var ns = root.Name.Namespace;
        var container = root.Element(ns + "Permissions");
        if (container == null) return Array.Empty<BcAppSymbolCache.PermissionSymbol>();

        var rows = new List<BcAppSymbolCache.PermissionSymbol>();
        foreach (var p in container.Elements(ns + "Permission"))
        {
            // A row missing any of the three cannot mean anything, so it is dropped rather
            // than guessed at — but it is dropped WITH A DIAGNOSTIC, on the same
            // AL_RUNNER_DIAG_PERMMETA channel the id route below uses. #3609 was itself a
            // grant dropped by a `continue` that no verbosity could see, and "this cannot
            // happen for a document the compiler produced" is exactly the reasoning that made
            // the original one invisible. If BC's emitter ever changes shape here, the drop
            // has to be greppable rather than silent.
            var type = (string?)p.Attribute("Type");
            var idText = (string?)p.Attribute("ID");
            var valueText = (string?)p.Attribute("Value");
            if (!TryParseInt(type, out var typeOrdinal)
                || !TryParseInt(idText, out var id)
                || !TryParseInt(valueText, out var value))
            {
                if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_PERMMETA") == "1")
                    Console.Error.WriteLine(
                        "[perm-metadata] BC's permission-set document carries a <Permission> row "
                        + $"this code cannot read (Type='{type}' ID='{idText}' Value='{valueText}') "
                        + "— dropped rather than guessed");
                continue;
            }
            rows.Add(new BcAppSymbolCache.PermissionSymbol(typeOrdinal, id, value));
        }
        return rows;
    }

    /// <summary>
    /// An <c>IncludedPermissionSets</c>/<c>ExcludedPermissionSets</c> attribute. BC states
    /// resolved object IDS here, comma-separated — not the quoted names AL source and
    /// SymbolReference.json state — so these are carried as ids and never go through
    /// PermissionSetIdByName's lookup-and-drop.
    /// </summary>
    private static IReadOnlyList<int>? ReadPermissionSetIdList(XElement root, string attribute)
    {
        var raw = (string?)root.Attribute(attribute);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Same rule as ReadPermissions above: an unparseable entry is dropped, and the drop is
        // observable on AL_RUNNER_DIAG_PERMMETA rather than silent. A lost include edge makes
        // BC compose a DIFFERENT set of grants, which is precisely the class of wrong answer
        // nothing downstream can detect.
        var ids = new List<int>();
        foreach (var part in raw.Split(','))
        {
            var text = part.Trim();
            if (text.Length == 0) continue;          // trailing separator, not a lost edge
            if (TryParseInt(text, out var id)) { ids.Add(id); continue; }
            if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_PERMMETA") == "1")
                Console.Error.WriteLine(
                    $"[perm-metadata] BC's permission-set document states {attribute}='{raw}', whose "
                    + $"entry '{text}' is not an object id this code can read — dropped rather than guessed");
        }
        return ids.Count == 0 ? null : ids;
    }

    private static bool TryParseInt(string? text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
