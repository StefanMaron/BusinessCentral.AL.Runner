// RecordPatches.PermissionSetFromBcDocument — a source-compiled permission set's declaration
// read from BC's OWN emitted metadata document instead of re-derived from AL source text
// (issue #3609, the last conversion of the chain #3562 tracks).
//
// THE SEAM
//   BC's Compilation.Emit hands CaptureOutputter a <PermissionSet> document for every
//   `permissionset` object it emits, and AlObjectMetadataRegistry (#3548) keeps it keyed
//   (kind, id). Measured on BC 28.1, a two-set probe:
//
//     <PermissionSet ID="70702" Name="PP Derived" Access="Internal" Assignable="1"
//                    CaptionML="ENU=PP derived caption" IncludedPermissionSets="70701"
//                    ExcludedPermissionSets="70704">
//       <Permission Type="0" ID="70700" Value="449" />
//       <Permission Type="5" ID="70703" Value="16" />
//     </PermissionSet>
//
//   That states every column RecordPatches.AlPermissionSetParser derived by regex, and it
//   states two of them RESOLVED that the parser could only state as names — see the two
//   claims below. docs/permission-set-from-bc-document.md has the probe and the measurements.
//
// CLAIM: object references arrive as IDS, which removes a silent wrong answer.
//   A source-declared `Permissions = tabledata "PP Thing" = RIMD` names its object; AL has no
//   id form. ResolveSourcePermissionEntries resolved that name against ParsedObjectDecls,
//   which does not carry TABLES — so `AlKeywordForPermissionObject` returned null for
//   ordinals 0 (tabledata) and 1 (table) and the entry was dropped by `if (kind == null)
//   continue;`, BEFORE the diagnostic below it. Every tabledata grant in a source-compiled
//   permission set therefore vanished at every verbosity, including AL_RUNNER_DIAG_PERMMETA=1.
//   BC's document states `Type="0" ID="70700"` outright, so the resolution step — and its
//   blind spot — is not needed on this route.
//
// CLAIM: masks arrive computed, and BC's encoding is the one the parser reimplemented.
//   Measured: `RIMD` → Value="15", `Rimd` → Value="449", `X` → Value="16", matching
//   MaskFromAlLetters' R=1 I=2 M=4 D=8 X=16 / r=32 i=64 m=128 d=256 x=512 exactly. So this
//   route is not a different answer, it is the same answer without the transcription.
//
// CLAIM: IncludedPermissionSets/ExcludedPermissionSets arrive as ids, not names.
//   BuildIncludeList resolves names against this run's inventory and DROPS what it cannot
//   find. BC states the resolved object id, so a set included by a source-compiled
//   declaration cannot be lost to a name lookup on this route.
//
// EXCLUDEDPERMISSIONSETS — WHAT THE 0-OF-258 MEASUREMENT DOES AND DOES NOT SAY
//   Issue #3609 records that ExcludedPermissionSets occurs 0 times across Base Application's
//   258 permission sets, and expected conversion to leave that column unfixed. Re-measured
//   here, the count is right and the inference from it is not: 258 permissionset sources,
//   0 declaring ExcludedPermissionSets, 46 declaring IncludedPermissionSets. The zero is a
//   property of Base Application's own AL, not of BC's emitter — a probe declaring the
//   property gets `ExcludedPermissionSets="70704"` on the document alongside
//   IncludedPermissionSets. So this route DOES carry it, and the populator's hardcoded null
//   is retired for source-compiled sets rather than kept. It stays for the precompiled route,
//   where SymbolReference.json is what the runner reads and the column is not extracted.
//
// WHAT SURVIVES CONVERSION — nothing here becomes dead code
//   Issue #3609 expected RecordPatches.AlPermissionSetParser.cs (176 lines) and part of
//   PermissionMetadataPopulator.cs to retire. Measured, neither can, and the reason is
//   structural rather than incidental:
//
//     - The registry is keyed BY ID, so something has to say which permission-set ids this
//       run declares before a document can be looked up. The parser is that inventory.
//     - BC's document does not state the OWNING APP. AppId/AppName come from the app.json
//       that owns the source file (ResolveOwningApp), and both the "Metadata Permission Set"
//       App ID column and AggregatePermissionSetVirtualTable.BuildKnownAppNameIndex read them.
//     - Emit runs only on a compile-cache MISS, so the derivation is the live fallback on
//       every warm run whose replay supplied no document — not a legacy path.
//
//   What conversion removes is the derivation's ROLE as the answer, not its code: the
//   name→id resolution, the hand-rolled mask table and the include-name lookup stop deciding
//   what a source-compiled permission set means whenever BC's document is available.
//   docs/permission-set-from-bc-document.md § "what survives" has the per-symbol detail.
//
// AVAILABILITY DECIDES THE ROUTE, AND A FAILURE IS NEVER RE-ROUTED
//   Taken only when a document is registered for (PermissionSet, id). A permission set from a
//   precompiled dependency .app has none — it is read from SymbolReference.json — and keeps
//   that route unchanged. Nothing here catches a parse failure and falls back to the regex
//   derivation: a weaker answer substituted on error is wrong metadata under a green build
//   (.claude/rules/loud-failures.md).
//
// CACHE-HIT SAFETY
//   Emit runs only on a compile-cache MISS. The registry is replayed on a HIT by the three
//   mechanisms ObjectMetadataRegistry.cs documents (enum-registry sidecar, dependency
//   .object-metadata.json, --watch shadow snapshot), and HasBcPermissionSetDocument answers
//   false when none of them supplied one — so a warm run with no replayed document keeps the
//   AL-source derivation rather than silently losing every source-declared permission set.
//
// PRECOMPILED-DLL RESPECT: this file reads an XML string the compiler already produced and
// builds the runner's own PermissionSetSymbol record from it. No BC type is constructed or
// rewritten here.

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
            // Assignable is emitted as "1"/"0". AL's own default when a set declares none is
            // true, and BC states the attribute on every set the compiler emits — so an
            // absent attribute takes AL's default rather than reading as false.
            ParseBcBool((string?)root.Attribute("Assignable"), defaultValue: true),
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
            // A row missing any of the three is not a row that can mean anything; skipping it
            // is the same conservative choice the name-resolving route made, but it cannot
            // happen for a document the compiler produced.
            if (!TryParseInt((string?)p.Attribute("Type"), out var type)) continue;
            if (!TryParseInt((string?)p.Attribute("ID"), out var id)) continue;
            if (!TryParseInt((string?)p.Attribute("Value"), out var value)) continue;
            rows.Add(new BcAppSymbolCache.PermissionSymbol(type, id, value));
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

        var ids = new List<int>();
        foreach (var part in raw.Split(','))
            if (TryParseInt(part.Trim(), out var id))
                ids.Add(id);
        return ids.Count == 0 ? null : ids;
    }

    private static bool TryParseInt(string? text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
