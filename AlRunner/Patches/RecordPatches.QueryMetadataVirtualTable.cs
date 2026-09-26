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
// DELIBERATELY QUERY + XMLPORT ONLY (XMLport Metadata, 2000000280, joined in #4461 — its
// XmlPortDataProvider has exactly QueryDataProvider's shape) — and the reason is NOT the one it first appears to be. That snapshot
// has TEN direct callers; #4196's "17" is the count for GetObjectNumberAndInfoWithinRange, a
// different method one level up, and the two sets are nearly disjoint. Re-derived on bc284
// (#4447): NINE of the ten are on NCLMetadata/MetadataDataProvider themselves —
// CountObjectsWithinRange, GetObjectName, GetObjectId, GetMetaTableByName, GetObjByFullName,
// IsTableNameAmbigous, InitializeAppGroupObjects and the two iterator MoveNexts
// (<GetObjectNumberAndInfoWithinRange>d__10, <GetObjectTypesWithinRange>d__9) — plus
// Debugger.HeuristicProfilerActivityContext.GetRunObjectDescription. All ten are
// type-agnostic in the sense that matters: they index the outer SortedList by ObjectType and
// would see a Query entry.
//
// An earlier revision of this comment named AllObjDataProvider, AllObjWithCaptionDataProvider
// and SystemObjectDataProvider here. Those three are real, and they call the method one level
// up — they are NOT among this snapshot's ten. Corrected rather than deleted because the
// conclusion below is unchanged and was never resting on the three names.
//
// What actually protects AllObj (2000000038), AllObjWithCaption (2000000058), Field
// (2000000041), Table Metadata (2000000136), Page Metadata (2000000138) and Page Control Field
// (2000000139) is that the runner never hands out those BC providers at all: each goes through
// a hand-written Populate* in RecordPatches. **The dispatch is the brace, not this restriction.**
// Measured in review: widening this helper to ObjectType.Table left all four protected corpus
// codeunits green, so the narrowing here is defensive rather than load-bearing.
//
// That distinction matters for #4196, where the restriction goes away and the dispatch becomes
// the only thing holding those six tables — so the dispatch is what that work has to re-measure,
// not this comment's type filter.
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
using Microsoft.Dynamics.Nav.Runtime;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int QueryMetadataVirtualTableId = 2000000142;

    private static bool IsQueryMetadataVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == QueryMetadataVirtualTableId;

    /// <summary>
    /// The DataAccess BC's own factory builds for 2000000142 — one over QueryDataProvider.
    ///
    /// <para>Without this branch the table falls through to the empty temp store every table
    /// reaches by default, so the snapshot substitution below is necessary but not sufficient:
    /// QueryDataProvider is never constructed, so nothing ever reads the snapshot. Both halves
    /// are required, and neither shows up as a failure the other would explain.</para>
    /// </summary>
    internal static object GetQueryMetadataVirtualDataAccess(object dataAccessSource, NCLMetaTable table)
        => GetBcVirtualDataAccess(dataAccessSource, table,
            "every Record \"Query Metadata\" read would answer from an empty store");

    private static MethodInfo? _qmTryGetMetaQuery;
    private static Type? _qmSnapshotOuter, _qmSnapshotInner, _qmEntryType, _qmObjectTypeEnum;

    /// <summary>
    /// Body of <c>NCLMetadata.GetSnapshotOfAllObjects(int)</c> (Cecil-replaced in
    /// NclCecilRewrite.Records.cs, replacing the empty-list replacement that stands in for a
    /// real impl which NREs in skeleton mode).
    ///
    /// <para>Returns the same <c>SortedList&lt;ObjectType, SortedList&lt;int,
    /// AllObjectSnapshotEntry&gt;&gt;</c> BC expects, carrying TWO entries —
    /// <c>ObjectType.Query</c> from <see cref="KnownQueryIdSet"/> and <c>ObjectType.XmlPort</c>
    /// from <see cref="KnownXmlPortIdSet"/> (#4461). Every other object type is absent, so every
    /// other caller sees exactly the empty snapshot it saw before (#4196 tracks widening this).</para>
    /// </summary>
    public static object NCLMetadata_GetSnapshotOfAllObjects(object self, int arg)
    {
        EnsureQuerySnapshotReflection(self);

        // The outer SortedList BC's callers index by ObjectType. Built fresh per call, as BC's
        // own body does: the ids can grow as dependencies register, and a cached instance would
        // pin the first answer for the process.
        var outer = (IDictionary)Activator.CreateInstance(_qmSnapshotOuter!)!;

        // Filtered here, never in KnownQueryIdSet / KnownXmlPortIdSet: both are memoized on a
        // generation tuple with no app-group term, so a sibling group's object stays in them and
        // a filter applied there would cache the FIRST group's answer for the process. This
        // builds the snapshot on every call, which is what makes a per-call scope read correct
        // (#4447). Both tables' BC providers build every column themselves and reach the ids only
        // through this snapshot, so the snapshot IS the filter point.
        var visibleApps = CurrentVisibleAppClosure();

        AddSnapshotEntries(outer, ObjectTypeQuery, "Query", KnownQueryIdSet(), visibleApps,
            id => QueryMetadataResolves(self, id));
        // #4461: XMLport Metadata (2000000280). XmlPortDataProvider has the same shape as
        // QueryDataProvider: GetObjectNumberAndInfoWithinRange(ObjectType.XmlPort, …, needNames:
        // false), then every column from the NCLMetaXmlPort the metadata cache resolves.
        Dictionary<(string Kind, int Id), Guid>? ownerIndex = null;
        Dictionary<int, Guid>? compiledSourceOwners = null;
        AddSnapshotEntries(outer, ObjectTypeXmlPort, "XmlPort", KnownXmlPortIdSet(), visibleApps,
            id => !IsCompiledXmlPortOfUnreachableSourceApp(id, visibleApps, ref compiledSourceOwners)
                  && XmlPortMetadataResolves(id, ref ownerIndex));
        return outer;
    }

    private static void AddSnapshotEntries(IDictionary outer, int objectType, string kind,
        HashSet<int> ids, HashSet<Guid>? visibleApps, Func<int, bool> resolves)
    {
        if (ids.Count == 0) return;
        var inner = (IDictionary)Activator.CreateInstance(_qmSnapshotInner!)!;
        foreach (var id in ids)
        {
            if (IsHiddenFromCurrentAppGroup(kind, id, visibleApps)) continue;
            // Admitted by BC's own rule, not ours: an id must resolve through the metadata
            // cache to appear, as BC's provider bodies skip one that throws
            // NavMetadataNotFoundException.
            if (!resolves(id)) continue;
            // Name is left empty, and this is LOAD-BEARING. Both providers call this walk with
            // needNames:false and read the name off the metadata object, and an empty name keeps
            // the entry out of EnsureAllObjectNamesWillBeUnique's duplicate-name check, which
            // skips on IsNullOrWhiteSpace.
            var entry = Activator.CreateInstance(_qmEntryType!, new object?[] { string.Empty, null, null, string.Empty });
            if (entry != null) inner[id] = entry;
        }
        outer[Enum.ToObject(_qmObjectTypeEnum!, objectType)] = inner;
    }

    /// <summary>ObjectType.Query — 9, the same constant the metadata-cache populator uses.</summary>
    private const int ObjectTypeQuery = 9;

    private static bool QueryMetadataResolves(object? nclMetadata, int id)
    {
        if (nclMetadata == null || _qmTryGetMetaQuery == null) return false;
        try
        {
            // requireCompiled: false — the row reports declared metadata, which exists whether or
            // not the query has been compiled. appGroupId: -1, BC's own default for "this group",
            // passed explicitly because MethodInfo.Invoke does not apply C# defaults.
            var args = new object?[] { id, null, false, -1 };
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

        // `self` IS the NCLMetadata instance — this helper replaces a method ON NCLMetadata —
        // so TryGetMetaQueryById is bound on `t` directly. There is no NclMetadata property to
        // hop through, and an earlier shape that looked for one bound nothing.
        //
        // FOUR parameters, not three: the signature is
        // TryGetMetaQueryById(int queryId, out NCLMetaQuery, bool requireCompiled, int appGroupId).
        // The last has a C# default, so BC's own decompiled call sites show three arguments and
        // reading the arity off one of them binds NOTHING — the same trap as
        // GetSnapshotOfAllObjects(int appGroupId = -1) itself, whose callers show no argument at
        // all. MethodInfo.Invoke does not apply C# defaults, so the value is passed explicitly
        // below. Sibling KeyVirtualTable binds its TryGetMetaTableById at 4 for the same reason.
        var tryGet = t.GetMethods(inst).FirstOrDefault(m =>
            m.Name == "TryGetMetaQueryById" && m.GetParameters().Length == 4
            && m.GetParameters()[0].ParameterType == typeof(int));

        if (outer == null || inner == null || entry == null || objectTypeEnum == null || tryGet == null)
            // BcShapeGapException, not InvalidOperationException: NavMethodScope_AssertError
            // rethrows only this type, so an `asserterror` around a driver hitting this refusal
            // would otherwise SWALLOW it and PASS — inverting the result (#2946).
            throw new BcShapeGapException(
                Surface, "NCLMetadata",
                "BC does not expose the shape the Query Metadata snapshot substitution drives "
                + "(GetSnapshotOfAllObjects returning SortedList<ObjectType, SortedList<int, "
                + "AllObjectSnapshotEntry>>, TryGetMetaQueryById(int, out, bool, int)) — see #4147");

        _qmSnapshotOuter = outer;
        _qmSnapshotInner = inner;
        _qmEntryType = entry;
        _qmObjectTypeEnum = objectTypeEnum;
        _qmTryGetMetaQuery = tryGet;
    }
}
