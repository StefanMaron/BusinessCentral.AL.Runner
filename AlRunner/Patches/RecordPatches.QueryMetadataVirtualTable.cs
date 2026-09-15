// RecordPatches.QueryMetadataVirtualTable — the "Query Metadata" system virtual table
// (2000000142) is served by BC's OWN QueryDataProvider, with ONE substitution.
//
// Why this table is not the shape Key (#4191) and Page Action (#4192) used. Those replace the
// provider's GetValuesWithinRangeForKeyField and rebuild the row. QueryDataProvider cannot be
// served that way: it builds its 13-slot row INLINE and hands it to
//
//     ReadOnlyRecordBuffer CreateVirtualRecordBuffer(ReadOnlySpan<NavValue> values)
//
// and a ref struct CANNOT be passed through MethodInfo.Invoke at all — so a reflection-based
// helper has no way to hand the row back. (That member also has four overloads on
// VirtualDataProvider, which BcShape.FindMethod refuses on rather than guessing through.)
//
// So the substitution moves one level down, to the single line that is actually defective.
// BC's body is:
//
//     foreach (var item in GetObjectNumberAndInfoWithinRange(ObjectType.Query, range, sortOrder,
//                                                            needNames: false))
//         ... build the row from NCLMetaQuery ...
//
// and GetObjectNumberAndInfoWithinRange reads NCLMetadata.GetSnapshotOfAllObjects(), whose body
// NclCecilRewrite.Records.cs replaces with an EMPTY SortedList. Every column of every row is
// already BC's own; the only thing missing is the id list to iterate. Filling the snapshot for
// ObjectType.Query alone therefore fixes this table with no row-building whatsoever, and BC's
// own bounds/binary-search/sort-order/obsolete-filter traversal keeps running unchanged.
//
// DELIBERATELY QUERY-ONLY. That same snapshot has 17 callers (#4196), six of which drive tables
// the runner already serves correctly through hand-written populators — AllObj (2000000038),
// AllObjWithCaption (2000000058), Field (2000000041), Table Metadata (2000000136), Page
// Metadata (2000000138), Page Control Field (2000000139). Those work BECAUSE the snapshot is
// empty and they never consult it. Filling it for every type is the consolidation #4196 tracks
// and needs its own measurement; filling it for ObjectType.Query changes exactly one table,
// because no other caller asks for that type.
//
// WHAT THE CORPUS PINNED (corpus codeunit 60913, 6 arms, 16 legs green):
//   - Caption falls back to the NAME when the query declares none (BC's own
//     `nCLMetaQuery.Caption ?? nCLMetaQuery.Name`), rather than answering blank;
//   - the API columns are set on a QueryType = API query and blank on a Normal one, both
//     directions, so a provider that never filled them cannot pass;
//   - the row carries the declaring extension's app id, not the empty GUID.
// None of those are asserted by this file — they are BC's, and that is the point.
using System.Collections;
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int QueryMetadataVirtualTableId = 2000000142;

    private static MethodInfo? _qmTryGetMetaQuery;
    private static PropertyInfo? _qmNclMetadata;
    private static Type? _qmSnapshotOuter, _qmSnapshotInner, _qmEntryType, _qmObjectTypeEnum;

    /// <summary>
    /// Body of <c>NCLMetadata.GetSnapshotOfAllObjects(int)</c> (Cecil-replaced in
    /// NclCecilRewrite.Records.cs, replacing the empty-list replacement that stands in for a
    /// real impl which NREs in skeleton mode).
    ///
    /// <para>Returns the same <c>SortedList&lt;ObjectType, SortedList&lt;int,
    /// AllObjectSnapshotEntry&gt;&gt;</c> BC expects, carrying ONE entry —
    /// <c>ObjectType.Query</c> — populated from <see cref="KnownQueryIdSet"/>. Every other
    /// object type is absent, so every other caller sees exactly the empty snapshot it sees
    /// today and cannot change behaviour (#4196 tracks widening this).</para>
    /// </summary>
    public static object NCLMetadata_GetSnapshotOfAllObjects(object self, int arg)
    {
        EnsureQuerySnapshotReflection(self);

        // The outer SortedList BC's callers index by ObjectType. Built fresh per call, as BC's
        // own body does: the ids can grow as dependencies register, and a cached instance would
        // pin the first answer for the process.
        var outer = (IDictionary)Activator.CreateInstance(_qmSnapshotOuter!)!;

        var ids = KnownQueryIdSet();
        if (ids.Count == 0) return outer;

        var inner = (IDictionary)Activator.CreateInstance(_qmSnapshotInner!)!;
        var nclMetadata = _qmNclMetadata?.GetValue(self);
        foreach (var id in ids)
        {
            // Admitted by BC's own rule, not ours: an id must resolve through the metadata
            // cache to appear. An id the runner knows exists but cannot build metadata for is
            // skipped here rather than surfacing as a row whose every column would be empty —
            // and BC's own body would skip it too, via its catch (NavMetadataNotFoundException).
            if (!QueryMetadataResolves(nclMetadata, id)) continue;
            // Name is left empty: BC calls this walk with needNames:false for ObjectType.Query
            // and reads the name off NCLMetaQuery instead, so a name here would be dead weight
            // that could only disagree with the one the row actually carries.
            var entry = Activator.CreateInstance(_qmEntryType!, new object?[] { string.Empty, null, null, string.Empty });
            if (entry != null) inner[id] = entry;
        }

        outer[Enum.ToObject(_qmObjectTypeEnum!, ObjectTypeQuery)] = inner;
        return outer;
    }

    /// <summary>ObjectType.Query — 9, the same constant the metadata-cache populator uses.</summary>
    private const int ObjectTypeQuery = 9;

    private static bool QueryMetadataResolves(object? nclMetadata, int id)
    {
        if (nclMetadata == null || _qmTryGetMetaQuery == null) return false;
        try
        {
            var args = new object?[] { id, null, false };
            return (bool)_qmTryGetMetaQuery.Invoke(nclMetadata, args)! && args[1] != null;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureQuerySnapshotReflection(object self)
    {
        if (_qmSnapshotOuter != null) return;
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const string Surface = "query-metadata-virtual-table";
        const string Detail = "BC shape changed; see #4147";

        var t = self.GetType();
        var getSnap = BcShape.FindMethod(t, "GetSnapshotOfAllObjects", inst, Surface, "GetSnapshotOfAllObjects", Detail);
        // The return type IS the shape to build — read it off BC rather than naming the generic
        // arguments here, so a BC change in either type parameter refuses instead of silently
        // constructing the wrong dictionary.
        var outer = getSnap?.ReturnType;
        var inner = outer != null && outer.IsGenericType ? outer.GetGenericArguments().ElementAtOrDefault(1) : null;
        var entry = inner != null && inner.IsGenericType ? inner.GetGenericArguments().ElementAtOrDefault(1) : null;
        var objectTypeEnum = outer != null && outer.IsGenericType ? outer.GetGenericArguments().ElementAtOrDefault(0) : null;

        var nclMetadataProp = t.GetProperty("NclMetadata", inst) != null ? t.GetProperty("NclMetadata", inst) : null;
        var tryGet = t.GetMethods(inst).FirstOrDefault(m =>
            m.Name == "TryGetMetaQueryById" && m.GetParameters().Length == 3
            && m.GetParameters()[0].ParameterType == typeof(int));

        if (outer == null || inner == null || entry == null || objectTypeEnum == null || tryGet == null)
            // BcShapeGapException, not InvalidOperationException: NavMethodScope_AssertError
            // rethrows only this type, so an `asserterror` around a driver hitting this refusal
            // would otherwise SWALLOW it and PASS — inverting the result (#2946).
            throw new BcShapeGapException(
                Surface, "NCLMetadata",
                "BC does not expose the shape the Query Metadata snapshot substitution drives "
                + "(GetSnapshotOfAllObjects returning SortedList<ObjectType, SortedList<int, "
                + "AllObjectSnapshotEntry>>, TryGetMetaQueryById(int, out, bool)) — see #4147");

        _qmSnapshotOuter = outer;
        _qmSnapshotInner = inner;
        _qmEntryType = entry;
        _qmObjectTypeEnum = objectTypeEnum;
        _qmNclMetadata = nclMetadataProp;
        _qmTryGetMetaQuery = tryGet;
    }
}
