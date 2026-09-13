// RecordPatches.TableRelationsMetadataVirtualTable — the "Table Relations Metadata" system
// virtual table (2000000141) is served by BC's OWN TableRelationDataProvider, through the same
// GetBcVirtualDataAccess factory bind Integer and Date use (RecordPatches.IntegerVirtualTable.cs).
// Before #4088 the table fell through to an empty temp store, so Base Application's
// "Config. Template Management".GetLookupParameters read IsEmpty() = true and never opened a lookup.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int TableRelationsMetadataVirtualTableId = 2000000141;

    private static bool IsTableRelationsMetadataVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == TableRelationsMetadataVirtualTableId;

    /// <summary>The DataAccess BC's own factory builds for 2000000141 — one over TableRelationDataProvider.</summary>
    internal static object GetTableRelationsMetadataVirtualDataAccess(object dataAccessSource, NCLMetaTable table)
        => GetBcVirtualDataAccess(dataAccessSource, table,
            "every Record \"Table Relations Metadata\" read would answer from an empty store");

    private static MethodInfo? _trmGetFieldNos, _trmGetRelationNos, _trmGetConditionNos, _trmCreateEntry;
    private static MethodInfo? _trmGetBounds, _trmTryGetMetaTable;
    private static PropertyInfo? _trmNclMetadata;
    private static Type? _trmBufferType;

    /// <summary>
    /// Body of TableRelationDataProvider.GetValuesWithinRangeForKeyField (Cecil-replaced in
    /// NclCecilRewrite.Runtime.cs). Key fields 2..4 forward to BC's own private iterators
    /// unchanged. Key field 1 is BC's GetTableIDs with one substitution: the candidate table ids
    /// come from the runner's table inventory instead of NCLMetadata.GetSnapshotOfAllObjects,
    /// whose body is Cecil-replaced with an empty list. Each candidate is still admitted by
    /// BC's own rule — it resolves through NCLMetadata.TryGetMetaTableById(requireCompiled:
    /// false) and is not ObsoleteState = Removed — and its row is built by BC's own
    /// CreateNewTableRelationEntry, so every column is BC's.
    /// </summary>
    public static object TableRelationDataProvider_GetValuesWithinRangeForKeyField(
        object self, object keyField, object precedingKeyFieldValues, object range, object sortOrder, object nonPrimaryKeyFilters)
    {
        EnsureTableRelationsReflection(self);
        var fieldNo = ((NCLMetaField)keyField).FieldNo;
        object? result = fieldNo switch
        {
            1 => TableRelationsTableIds(self, range, sortOrder),
            2 => _trmGetFieldNos!.Invoke(self, new[] { range, sortOrder, precedingKeyFieldValues }),
            3 => _trmGetRelationNos!.Invoke(self, new[] { range, sortOrder, precedingKeyFieldValues }),
            4 => _trmGetConditionNos!.Invoke(self, new[] { range, sortOrder, precedingKeyFieldValues, nonPrimaryKeyFilters }),
            _ => throw new NotSupportedException(),
        };
        return result!;
    }

    private static object TableRelationsTableIds(object provider, object range, object sortOrder)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(_trmBufferType!))!;

        var boundsArgs = new object?[] { 0, int.MaxValue, 0, 0 };
        if (!(bool)_trmGetBounds!.Invoke(range, boundsArgs)!) return list;
        int low = (int)boundsArgs[2]!, high = (int)boundsArgs[3]!;

        IEnumerable<int> candidates = low == high
            ? new[] { low }
            : EnumerateKnownTableMetadata().Select(r => r.Id).Where(id => id >= low && id <= high).Distinct();
        var ordered = sortOrder.ToString() == "Descending"
            ? candidates.OrderByDescending(id => id)
            : candidates.OrderBy(id => id);

        var nclMetadata = _trmNclMetadata!.GetValue(provider);
        foreach (var id in ordered)
        {
            var args = new object?[] { id, null, false, 0 };
            if (!(bool)_trmTryGetMetaTable!.Invoke(nclMetadata, args)!) continue;
            if (args[1] is not NCLMetaTable meta || meta.ObsoleteState == Microsoft.Dynamics.Nav.Types.Metadata.ObsoleteState.Removed) continue;
            var row = _trmCreateEntry!.Invoke(provider, new object?[]
            {
                NavInteger.Create(id), NavText.Create(meta.TableName), null, null, null, null, null,
            });
            if (row != null) list.Add(row);
        }
        return list;
    }

    private static void EnsureTableRelationsReflection(object provider)
    {
        if (_trmCreateEntry != null) return;
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var t = provider.GetType();
        // BcShape.FindMethod, not Type.GetMethod: a name-only GetMethod hands back the
        // most-derived declaration when BC `new`-hides a member, and drives the wrong one
        // silently. FindMethod refuses on an ambiguity and returns null on absence, which is
        // what the joint null-check below already expects (#3069).
        const string Surface = "table-relations-metadata";
        const string Detail = "BC shape changed; see #4088";
        var getFieldNos = BcShape.FindMethod(t, "GetFieldNos", inst, Surface, "GetFieldNos", Detail);
        var getRelationNos = BcShape.FindMethod(t, "GetRelationNos", inst, Surface, "GetRelationNos", Detail);
        var getConditionNos = BcShape.FindMethod(t, "GetConditionNos", inst, Surface, "GetConditionNos", Detail);
        var createEntry = BcShape.FindMethod(t, "CreateNewTableRelationEntry", inst, Surface, "CreateNewTableRelationEntry", Detail);
        var nclMetadata = t.GetProperty("NclMetadata", inst);
        var rangeType = getFieldNos?.GetParameters()[0].ParameterType;
        var getBounds = rangeType == null ? null
            : BcShape.FindMethod(rangeType, "GetInclusiveIntegerBounds", inst, Surface, "GetInclusiveIntegerBounds", Detail);
        var tryGet = nclMetadata?.PropertyType.GetMethods(inst).FirstOrDefault(m =>
            m.Name == "TryGetMetaTableById" && m.GetParameters().Length == 4
            && m.GetParameters()[0].ParameterType == typeof(int));
        if (getFieldNos == null || getRelationNos == null || getConditionNos == null || createEntry == null
            || nclMetadata == null || getBounds == null || tryGet == null)
            throw new InvalidOperationException(
                "TableRelationDataProvider does not expose the shape the Table Relations Metadata "
                + "rewrite drives (GetFieldNos/GetRelationNos/GetConditionNos/CreateNewTableRelationEntry, "
                + "NclMetadata, Range.GetInclusiveIntegerBounds, NCLMetadata.TryGetMetaTableById(int, out, bool, int)) — "
                + "BC shape changed; see #4088.");
        _trmBufferType = createEntry.ReturnType;
        _trmGetFieldNos = getFieldNos;
        _trmGetRelationNos = getRelationNos;
        _trmGetConditionNos = getConditionNos;
        _trmNclMetadata = nclMetadata;
        _trmGetBounds = getBounds;
        _trmTryGetMetaTable = tryGet;
        _trmCreateEntry = createEntry;
    }
}
