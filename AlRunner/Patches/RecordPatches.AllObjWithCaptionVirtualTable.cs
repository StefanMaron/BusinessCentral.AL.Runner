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
//   only addition is the caption.
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
            ?? throw new RunnerOutOfScopeException(
                "AllObjWithCaption (virtual table 2000000058)",
                "allobjwithcaption-virtual-table — data access has no in-memory provider; see docs/scope.md");

        // The Object Type option ordinals live on AllObjWithCaption's OWN field 1, not on
        // AllObj's: the two tables declare the same option set today, but reading the
        // ordinals off the table being populated is what keeps that an observation rather
        // than an assumption.
        var ordinals = EnsureAllObjWithCaptionObjectTypeOrdinals(metaTable);
        var done = _awcPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<(int, int), byte>());

        foreach (var (kind, id, name, caption) in EnumerateKnownAlObjects())
        {
            if (id <= 0) continue;
            if (!ordinals.TryGetValue(NormalizeObjectTypeName(kind), out var typeOrdinal))
                // This AL object kind has no ordinal in THIS BC version's option set.
                // Real BC would not list it either — skipping is faithful, inventing an
                // ordinal is not.
                continue;
            if (!done.TryAdd((typeOrdinal, id), 0))
                continue;

            InsertVirtualRow(provider, metaTable,
                new object[] { AllObjWithCaptionVirtualTableId, typeOrdinal, id, 0 },
                field => BuildAllObjWithCaptionValue(field, typeOrdinal, id, name,
                    // AL's own default caption is the object name. Applied here, once.
                    string.IsNullOrEmpty(caption) ? name : caption));
        }
    }

    /// <summary>
    /// One column of an AllObjWithCaption row, matched by the metatable's own FIELD NAME so
    /// the mapping tracks whatever the System package in the resolved artifact declares
    /// rather than a hardcoded field-number table. Every remaining column (App Package ID,
    /// App Runtime Package ID, Object Namespace, …) gets BC's own default, which is exactly
    /// what AllObjWithCaptionDataProvider emits for a base object with no app package and no
    /// namespace.
    ///
    /// <para><c>Object Subtype</c> is answered for PAGES, from the same PageType the "Page
    /// Metadata" (2000000138) virtual table reports — one source, so a page's subtype here
    /// and its PageType there cannot disagree. Base Application page 9170 "Profile Card"
    /// reads exactly this column: its <c>ValidateRoleCenterIdExists</c> does
    /// <c>AllObjWithCaption.TestField("Object Subtype", 'RoleCenter')</c> on the profile's
    /// Role Center ID, so a blank column made every profile insert refuse a legitimate role
    /// center. Other object kinds carry a subtype on a real tier too (a codeunit's
    /// Normal/Test/Install/Upgrade, a table's Normal/Temporary, …); those are NOT answered
    /// here and keep BC's default, because the runner has no equally-single source for them
    /// yet — see the issue linked from the PR that added this.</para>
    /// </summary>
    private static object? BuildAllObjWithCaptionValue(
        NCLMetaField field, int typeOrdinal, int objectId, string objectName, string objectCaption)
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
            {
                var subtype = SubtypeForObject(typeOrdinal, objectId);
                if (subtype == null) break;
                return _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, subtype });
            }
            default:
                break;
        }
        return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
    }

    /// <summary>
    /// The AL subtype of one object in the shared inventory, or null when the runner has no
    /// single authoritative source for that kind's subtype and BC's own default must stand.
    ///
    /// Pages only, deliberately — see <see cref="BuildAllObjWithCaptionValue"/>. The page's
    /// type ordinal is compared against the ordinal AllObjWithCaption's OWN "Object Type"
    /// option set gives "Page" in this artifact, never a hardcoded number.
    /// </summary>
    private static string? SubtypeForObject(int typeOrdinal, int objectId)
    {
        if (_awcObjectTypeOrdinals == null) return null;
        if (!_awcObjectTypeOrdinals.TryGetValue("page", out var pageOrdinal) || typeOrdinal != pageOrdinal)
            return null;
        var pageType = TryGetPageMetadataPageType(objectId);
        return string.IsNullOrEmpty(pageType) ? null : pageType;
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
            ?? throw new RunnerOutOfScopeException(
                "AllObjWithCaption (virtual table 2000000058)",
                "allobjwithcaption-virtual-table — metatable has no \"Object Type\" field, so its "
                + "option ordinals cannot be resolved; see docs/scope.md");

        var optionMetadata = typeField.FieldOptionMetadata
            ?? throw new RunnerOutOfScopeException(
                "AllObjWithCaption (virtual table 2000000058)",
                "allobjwithcaption-virtual-table — \"Object Type\" carries no option metadata; see docs/scope.md");

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
            throw new RunnerOutOfScopeException(
                "AllObjWithCaption (virtual table 2000000058)",
                $"allobjwithcaption-virtual-table — \"Object Type\" option string is empty ('{optionString}'); "
                + "see docs/scope.md");

        _awcObjectTypeOrdinals = map;
        return map;
    }
}
