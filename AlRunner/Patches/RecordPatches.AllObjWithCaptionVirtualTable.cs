// RecordPatches.AllObjWithCaptionVirtualTable — managed provider for the
// AllObjWithCaption system virtual table (2000000058).
//
// WHY THIS EXISTS
//   AllObjWithCaption is AllObj plus one column: Object Caption. It is virtual on the
//   service tier for the same reason AllObj is (its rows are computed from the metadata
//   of every published object), and it is the documented way for AL to put an object's
//   caption on screen — `SourceTable = AllObjWithCaption` lookup pages,
//   `TableRelation = AllObjWithCaption."Object ID"`, and
//   `CalcFormula = lookup(AllObjWithCaption."Object Caption" where(...))` FlowFields are
//   all ordinary AL.
//
//   The runner routes it to the same empty in-memory store as every other table, so
//   `Get(<type>, <id>)` was false for every object that has ever existed and every
//   caption lookup silently produced an empty string — a wrong answer, not an error, so
//   nothing upstream noticed. Pageworks reads it in five places (report and table caption
//   resolution in the layout studio and in the dataset designer); all of them rendered
//   blank.
//
// RELATIONSHIP TO THE AllObj PROVIDER
//   Same rows, same key, same construction path — this deliberately reuses AllObj's
//   inventory (EnumerateKnownAlObjects) and its reflection helpers rather than growing a
//   parallel one, so the two tables can never disagree about which objects exist. The
//   additions are the caption and the subtype (field 30, #2326), both COLUMNS AllObj does
//   not declare — its fields are 1/3/4/60/61/62. Both ride through the shared inventory for
//   this table's benefit; see ObjectSubtypeTextFor and
//   docs/virtual-tables-allobj.md#object-subtype.
//
// WHERE CAPTIONS COME FROM (two sources, neither invented)
//   1. Objects the runner compiles itself — the Caption property read off their AL source,
//      reached through SourceCaptionFor (RecordPatches.AlObjectCaptionParser.cs). Reports
//      are the one kind whose Caption is parsed by their own parser instead
//      (ParsedReport.Caption, RecordPatches.AlReportParser.cs, which needs it for the
//      Report Metadata virtual table anyway); SourceCaptionFor routes "Report" there, so
//      it is still one parse of the fact behind one accessor — see #1714.
//   2. Objects in a PRECOMPILED dependency — the Caption property recorded in that .app's
//      SymbolReference.json (BcAppSymbolCache.ObjectSymbol.Caption).
//   An object that declares NO Caption gets its object name, because that is AL's own
//   default caption and what a real tier reports — not an empty string. The "undeclared"
//   and "declared as the name" cases stay distinct all the way down to here so that
//   default is applied once, visibly, instead of being baked in at parse time.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only, reached through the helpers EnsureAllObjReflection
//   resolves. No AL business-logic body is touched.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// Every refusal in this file, built in one place. See
    /// RecordPatches.VirtualTableShapeGap.cs for the three-bucket classification and for
    /// why the anchor is "not-yet-implemented" rather than a docs/scope.md section (#2945).
    /// </summary>
    /// <remarks>
    /// Category (2) for all four. One is a store-wiring gap; the other three are BC metadata
    /// shapes this file reads rather than owns. Refusing beats guessing an option ordinal: the
    /// ordinal is a stored column value, so a wrong guess mis-keys every row it writes and no
    /// test can see it.
    /// </remarks>
    internal static RunnerOutOfScopeException AllObjWithCaptionShapeGap(string detail)
        => VirtualTableShapeGap("AllObjWithCaption (virtual table 2000000058)", "allobjwithcaption-virtual-table", detail);

    internal const int AllObjWithCaptionVirtualTableId = 2000000058;

    // Per in-memory-provider guard, so repeated data-access handouts within one test only
    // top up objects registered since (idempotent, no duplicate-key throws).
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<(int Type, int Id), byte>> _awcPopulatedByProvider = new();

    /// <summary>True if <paramref name="table"/> is AllObjWithCaption (2000000058).</summary>
    private static bool IsAllObjWithCaptionVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == AllObjWithCaptionVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind the AllObjWithCaption (2000000058) data access
    /// with one row per object the runner knows about. Idempotent per
    /// (provider, objectType, objectId); called on every handout so objects registered
    /// later in the run still show up.
    /// </summary>
    private static void PopulateAllObjWithCaptionVirtualTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw AllObjWithCaptionShapeGap("data access has no in-memory provider");

        // The Object Type option ordinals live on AllObjWithCaption's OWN field 1, not on
        // AllObj's: the two tables declare the same option set today, but reading the
        // ordinals off the table being populated is what keeps that an observation rather
        // than an assumption.
        var ordinals = EnsureAllObjWithCaptionObjectTypeOrdinals(metaTable);
        var done = _awcPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<(int, int), byte>());
        // Built lazily after the `done` guard, as PopulateAllObjVirtualTable does (#3117).
        Dictionary<(string Kind, int Id), Guid>? ownerIndex = null;

        foreach (var (kind, id, name, caption, subtype) in EnumerateKnownAlObjects())
        {
            if (id <= 0) continue;
            var normalized = NormalizeObjectTypeName(kind);
            if (!ordinals.TryGetValue(normalized, out var typeOrdinal))
                // This AL object kind has no ordinal in THIS BC version's option set.
                // Real BC would not list it either — skipping is faithful, inventing an
                // ordinal is not.
                continue;
            if (!done.TryAdd((typeOrdinal, id), 0))
                continue;
            // #3106: the same owner AllObj stamps, so the two tables agree on 60/61.
            ownerIndex ??= BuildObjectOwnerIndex();
            var owningAppId = ownerIndex.TryGetValue((normalized, id), out var owner) ? owner : Guid.Empty;

            InsertVirtualRow(provider, metaTable,
                new object[] { AllObjWithCaptionVirtualTableId, typeOrdinal, id, 0 },
                field => BuildAllObjWithCaptionValue(field, typeOrdinal, id, name,
                    // AL's own default caption is the object name. Applied here, once.
                    string.IsNullOrEmpty(caption) ? name : caption,
                    ObjectSubtypeTextFor(kind, subtype),
                    owningAppId,
                    AllObjWithCaptionKindCarriesAppId(kind)));
        }
    }

    /// <summary>
    /// One column of an AllObjWithCaption row, matched by the metatable's own FIELD NAME so
    /// the mapping tracks whatever the System package in the resolved artifact declares
    /// rather than a hardcoded field-number table. Every other column (AL Namespace, …) gets
    /// BC's own default, which is exactly what AllObjWithCaptionDataProvider emits for an
    /// object with no namespace.
    /// </summary>
    private static object? BuildAllObjWithCaptionValue(
        NCLMetaField field, int typeOrdinal, int objectId, string objectName, string objectCaption,
        string objectSubtype, Guid owningAppId, bool kindCarriesAppId)
    {
        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "objecttype":
                return _aovNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, typeOrdinal });
            case "objectid":
                return _aovNavIntegerCreate!.Invoke(null, new object?[] { objectId });
            case "objectname":
                return _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, objectName ?? string.Empty });
            case "objectcaption":
                return _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, objectCaption ?? string.Empty });
            case "objectsubtype":
                // Text[30] on this table, not an option — so the value is the enum MEMBER
                // NAME as text, which is what EnumHelper<T>.EnumToString hands BC's own
                // provider. Truncated through the field's own defined length, same as every
                // other text column here, rather than to a written-down 30.
                return _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, objectSubtype ?? string.Empty });
            // #3106. Observably equivalent to AllObjWithCaptionDataProvider: 60/61 come from the
            // same per-object entry AllObj reads, so they carry the values InsertAllObjRow writes;
            // App ID is the owning app's MANIFEST id (OwningApp.AppId), un-derived, and only for
            // the kinds GetCaptionAndSubtype resolves an owner for. An unknown owner stays
            // Guid.Empty, which is also BC's answer when OwningApp is null.
            // Corpus: 60802 AllObjWithCaption_*App* (corpus PR #329).
            case "apppackageid":
                return NavValue.CreateNavValueFromObject(field, AppPackageIdentity.PackageIdFor(owningAppId));
            case "appruntimepackageid":
                return NavValue.CreateNavValueFromObject(field, AppPackageIdentity.RuntimePackageIdFor(owningAppId));
            case "appid":
                return NavValue.CreateNavValueFromObject(field, kindCarriesAppId ? owningAppId : Guid.Empty);
            default:
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }

    /// <summary>
    /// True for the object kinds whose AllObjWithCaption "App ID" BC fills:
    /// AllObjWithCaptionDataProvider.GetCaptionAndSubtype sets the owner for TableData, Table,
    /// Report, XMLport, Page, Query, Codeunit and the five extension kinds it also resolves a
    /// target id for, and leaves it null for every other kind (Enum, PermissionSet, Profile,
    /// System, …) — same body in the 27.x and 28.4 Ncl.dll. Trap: do not widen this to "every
    /// kind with an owner"; an Enum's package columns are filled while its App ID is empty.
    /// </summary>
    internal static bool AllObjWithCaptionKindCarriesAppId(string kind)
        => NormalizeObjectTypeName(kind) switch
        {
            "tabledata" or "table" or "report" or "xmlport" or "page" or "query" or "codeunit" => true,
            _ => ExtensionTargetObjectKind(kind) != null,
        };

    /// <summary>
    /// The text BC puts in "Object Subtype" for one object, given the subtype the runner's
    /// inventory carries for it (<see cref="EnumerateKnownAlObjects"/>).
    ///
    /// <para>Observably equivalent to AllObjWithCaptionDataProvider.GetCaptionAndSubtype: a
    /// per-kind switch answering the member name, except that a CODEUNIT whose subtype is
    /// <c>Normal</c> answers the empty string — a table or query whose type is Normal
    /// answers the word — and the five *extension kinds answer the TARGET OBJECT'S ID as a
    /// decimal string instead of any name at all. Editing this method without both of those
    /// in hand gets it wrong in several directions at once. Per-kind table, the decompiled
    /// bodies, and why <c>Install</c> lands on empty: see
    /// docs/virtual-tables-allobj.md#object-subtype.</para>
    /// </summary>
    internal static string ObjectSubtypeTextFor(string kind, string? subtype)
    {
        if (string.IsNullOrEmpty(subtype)) return string.Empty;

        // The *extension kinds: `subtype` is the TARGET NAME the inventory carried, not a
        // subtype. BC reads TargetObjectId off the app group's object summary and renders it
        // invariantly; the runner has no app-group summary, so it resolves the same target
        // through the object inventory it does have. An unresolvable target answers the empty
        // string, which is BC's own `?? string.Empty` on that arm rather than a runner
        // invention.
        if (ExtensionTargetObjectKind(kind) is string targetKind)
        {
            var targetId = ResolveObjectIdOfKindByName(targetKind, subtype);
            return targetId > 0
                ? targetId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
        }

        if (NormalizeObjectTypeName(kind) != "codeunit") return subtype;

        // What the COMPILER wrote, not what the author declared — the same translation, in
        // the same position, as ResolveCodeunitSubtypeOrdinal. Install collapses to Normal
        // before the blanking test, so it cannot survive it.
        var effective =
            string.Equals(subtype, AlSubtypeTheCompilerDoesNotEmit, StringComparison.OrdinalIgnoreCase)
                ? AlDefaultCodeunitSubtype
                : subtype;

        // The one kind BC blanks when the subtype is its enum's default.
        return string.Equals(effective, AlDefaultCodeunitSubtype, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : effective;
    }

    /// <summary>
    /// For one of the five object kinds BC answers with a target id, the kind that target is
    /// resolved in; null for every other kind.
    ///
    /// <para>The mapping is the point: AL gives every object kind its OWN id namespace, so a
    /// pageextension's target must be looked up among PAGES and a tableextension's among
    /// TABLES. Resolving by name alone answers whichever object happens to match first, which
    /// is a plausible wrong number rather than a failure — the same hazard
    /// TryGetObjectNameOfKind exists for in the other direction (#2943).</para>
    /// <para>The set is BC's, taken from GetCaptionAndSubtype's shared switch arm, NOT
    /// "every kind whose name ends in Extension": <c>QueryExtension</c> is absent from that
    /// arm and from AllObjWithCaption's own Object Type option set, and
    /// <c>ProfileExtension</c> is in the option set but not in the arm. Both therefore keep
    /// the empty string.</para>
    /// </summary>
    internal static string? ExtensionTargetObjectKind(string kind)
        => NormalizeObjectTypeName(kind) switch
        {
            "pageextension" => "Page",
            "tableextension" => "Table",
            "enumextension" => "Enum",
            "permissionsetextension" => "PermissionSet",
            "reportextension" => "Report",
            _ => null,
        };

    /// <summary>
    /// The id of the object named <paramref name="objectName"/> WITHIN the kind
    /// <paramref name="kind"/>, or -1 when this run knows no such object.
    ///
    /// <para>Deliberately not <c>ResolveObjectIdByKindAndName</c>, which is otherwise the same
    /// question: that one walks <see cref="EnumerateKnownAlObjects"/>, and every caller of this
    /// method is ITSELF inside that walk — building an AllObjWithCaption row from an inventory
    /// item. Re-entering a running iterator over the same dictionaries is what
    /// <c>InvalidOperationException: Collection was modified</c> is made of, because
    /// ResolveTableIdByName writes to <c>_parsedTables</c> when it faults a dependency table
    /// in. So this reads the per-kind dictionaries directly and never the shared walk.</para>
    /// <para>Name comparison is exact and case-insensitive, matching how the dependency page
    /// index and BuildObjectIndexes compare — never the space-stripping NamesEqual, which
    /// would let "Item Attribute" and a hypothetical "ItemAttribute" answer for each other.</para>
    /// </summary>
    private static int ResolveObjectIdOfKindByName(string kind, string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName)) return -1;

        static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        switch (NormalizeObjectTypeName(kind))
        {
            case "table":
                foreach (var t in _parsedTables.Values)
                    if (Same(t.TableName, objectName)) return t.TableId;
                foreach (var t in EnumerateBcAppTableSymbols())
                    if (Same(t.TableName, objectName)) return t.TableId;
                return -1;
            case "page":
                foreach (var p in _parsedPages.Values)
                    if (Same(p.Name, objectName)) return p.Id;
                return ResolveDependencyObjectIdByName("Page", objectName);
            case "report":
                foreach (var r in _parsedReports.Values)
                    if (Same(r.Name, objectName)) return r.Id;
                return ResolveDependencyObjectIdByName("Report", objectName);
            case "enum":
                // AlEnumMetadataRegistry already merges this bundle's own enums with those
                // scanned out of dependency .apps, so it needs no dependency fallback.
                foreach (var e in AlEnumMetadataRegistry.Snapshot())
                    if (Same(e.Name, objectName)) return e.Id;
                return -1;
            case "permissionset":
                foreach (var d in _parsedObjectDecls.Values)
                    if (NormalizeObjectTypeName(d.Kind) == "permissionset" && Same(d.Name, objectName))
                        return d.Id;
                return ResolveDependencyObjectIdByName("PermissionSet", objectName);
            default:
                return -1;
        }
    }

    /// <summary>
    /// The id of a precompiled dependency object of <paramref name="kind"/> named
    /// <paramref name="objectName"/>, read off the registered .apps' flat object lists. -1
    /// when none matches. Used only for the kinds with no dedicated per-kind dependency index.
    /// </summary>
    private static int ResolveDependencyObjectIdByName(string kind, string objectName)
    {
        var wanted = NormalizeObjectTypeName(kind);
        foreach (var (_, symbols) in EnumerateRegisteredBcAppSymbols("extension target (AllObjWithCaption)"))
            foreach (var o in symbols.Objects)
                if (o.Id > 0
                    && NormalizeObjectTypeName(o.Kind) == wanted
                    && string.Equals(o.Name, objectName, StringComparison.OrdinalIgnoreCase))
                    return o.Id;
        return -1;
    }

    private static Dictionary<string, int>? _awcObjectTypeOrdinals;

    /// <summary>
    /// Read AllObjWithCaption's "Object Type" option ordinals out of the parsed metatable's
    /// own field-1 NCLOptionMetadata.OptionString, keyed by normalized option name — never
    /// a hardcoded table, and never borrowed from AllObj.
    /// </summary>
    private static Dictionary<string, int> EnsureAllObjWithCaptionObjectTypeOrdinals(NCLMetaTable metaTable)
    {
        if (_awcObjectTypeOrdinals != null) return _awcObjectTypeOrdinals;

        var typeField = (GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => NormalizeObjectTypeName(f.FieldName ?? string.Empty) == "objecttype")
            ?? throw AllObjWithCaptionShapeGap(
                "metatable has no \"Object Type\" field, so its option ordinals cannot be resolved");

        var optionMetadata = typeField.FieldOptionMetadata
            ?? throw AllObjWithCaptionShapeGap("\"Object Type\" carries no option metadata");

        var optionString = optionMetadata.OptionString ?? string.Empty;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var parts = optionString.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            var key = NormalizeObjectTypeName(parts[i]);
            if (key.Length == 0) continue;   // blank ordinals are real (reserved slots)
            map.TryAdd(key, i);
        }
        if (map.Count == 0)
            throw AllObjWithCaptionShapeGap($"\"Object Type\" option string is empty ('{optionString}')");

        _awcObjectTypeOrdinals = map;
        return map;
    }
}
