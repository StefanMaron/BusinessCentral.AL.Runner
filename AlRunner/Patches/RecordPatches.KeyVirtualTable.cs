// RecordPatches.KeyVirtualTable — the "Key" system virtual table (2000000063) is served by
// BC's OWN KeyDataProvider, through the same GetBcVirtualDataAccess factory bind Integer, Date
// and Table Relations Metadata use. Before #4147 the table had no branch at all and fell
// through to an empty temp store, so every Record "Key" read answered no rows, silently.
//
// Modelled on RecordPatches.TableRelationsMetadataVirtualTable.cs (#4088), which fixed the
// same shape for 2000000141. The two BC providers share a base (MetadataDataProvider) and the
// same defect against this runner: key field 1 enumerates objects through
// GetObjectNumberAndInfoWithinRange -> NCLMetadata.GetSnapshotOfAllObjects(), whose body
// NclCecilRewrite.Records.cs replaces with an empty list.
//
// WHAT THE CORPUS PINNED, and what a naive implementation gets wrong (corpus codeunit 60936,
// 6 arms, 8 cloud legs):
//   - a table declaring THREE keys reports FOUR rows. The extra one is BC's implicit SystemId
//     key, which is not in NCLMetaTable.Keys — so enumerating that list alone is short by one.
//   - that key's KeyFields column reads '$systemId', the SQL column name, NOT the AL field
//     name 'SystemId'.
// Neither is invented here: both come from BC's own GetKeysOnTable, which this file forwards
// to unchanged. They are recorded because they are the two ways a hand-rolled row builder
// would be wrong, and only the corpus arms would catch it.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int KeyVirtualTableId = 2000000063;

    private static bool IsKeyVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == KeyVirtualTableId;

    /// <summary>The DataAccess BC's own factory builds for 2000000063 — one over KeyDataProvider.</summary>
    internal static object GetKeyVirtualDataAccess(object dataAccessSource, NCLMetaTable table)
        => GetBcVirtualDataAccess(dataAccessSource, table,
            "every Record \"Key\" read would answer from an empty store");

    private static MethodInfo? _keyGetKeysOnTable, _keyGetBounds, _keyTryGetMetaTable;
    private static PropertyInfo? _keyNclMetadata;
    private static Type? _keyBufferType;

    /// <summary>
    /// Body of KeyDataProvider.GetValuesWithinRangeForKeyField (Cecil-replaced in
    /// NclCecilRewrite.Runtime.cs).
    ///
    /// <para>Key field 2 forwards to BC's own private GetKeysOnTable unchanged — it reads
    /// NCLMetaTable.Keys and needs nothing from the object snapshot, so every column of every
    /// key row is BC's own, including the implicit SystemId key and its '$systemId' spelling.</para>
    ///
    /// <para>Key field 1 is BC's own shape with one substitution: the candidate table ids come
    /// from the runner's table inventory instead of GetSnapshotOfAllObjects. Each candidate is
    /// still admitted by BC's own rule — it must resolve through
    /// NCLMetadata.TryGetMetaTableById(requireCompiled: false) — and the row carries only the
    /// two columns BC's own field-1 delegate sets: the table number and its name.</para>
    /// </summary>
    public static object KeyDataProvider_GetValuesWithinRangeForKeyField(
        object self, object keyField, object precedingKeyFieldValues, object range, object sortOrder, object nonPrimaryKeyFilters)
    {
        EnsureKeyReflection(self);
        var fieldNo = ((NCLMetaField)keyField).FieldNo;
        object? result = fieldNo switch
        {
            1 => KeyTableIds(self, range, sortOrder),
            // BC's own body indexes precedingKeyFieldValues[1] and [3] for the table number and
            // name — the two columns field 1 above sets. Forwarded positionally rather than
            // reconstructed, so the pair stays whatever BC's own field-1 row carried.
            2 => _keyGetKeysOnTable!.Invoke(self, new[]
            {
                ReadBufferSlot(precedingKeyFieldValues, 1),
                ReadBufferSlot(precedingKeyFieldValues, 3),
                range, sortOrder, nonPrimaryKeyFilters,
            }),
            _ => throw new NotSupportedException(),
        };
        return result!;
    }

    /// <summary>
    /// One slot of a ReadOnlyRecordBuffer. BC's own body uses the indexer; this reaches it by
    /// reflection because the type is internal to Ncl.
    /// </summary>
    private static object? ReadBufferSlot(object buffer, int index)
    {
        var indexer = buffer.GetType().GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return indexer?.GetValue(buffer, new object[] { index });
    }

    private static object KeyTableIds(object provider, object range, object sortOrder)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(_keyBufferType!))!;

        var boundsArgs = new object?[] { 0, int.MaxValue, 0, 0 };
        if (!(bool)_keyGetBounds!.Invoke(range, boundsArgs)!) return list;
        int low = (int)boundsArgs[2]!, high = (int)boundsArgs[3]!;

        IEnumerable<int> candidates = low == high
            ? new[] { low }
            : EnumerateKnownTableMetadata().Select(r => r.Id).Where(id => id >= low && id <= high).Distinct();
        var ordered = sortOrder.ToString() == "Descending"
            ? candidates.OrderByDescending(id => id)
            : candidates.OrderBy(id => id);

        var nclMetadata = _keyNclMetadata!.GetValue(provider);
        foreach (var id in ordered)
        {
            var args = new object?[] { id, null, false, 0 };
            if (!(bool)_keyTryGetMetaTable!.Invoke(nclMetadata, args)!) continue;
            if (args[1] is not NCLMetaTable meta) continue;
            // Slots 1 and 3 only, matching BC's own field-1 delegate: it allocates four slots
            // and sets the table number and the name, leaving 0 and 2 null.
            var slots = new NavValue?[4];
            slots[1] = NavInteger.Create(id);
            slots[3] = NavText.Create(meta.TableName);
            var row = Activator.CreateInstance(_keyBufferType!, new object?[] { slots });
            if (row != null) list.Add(row);
        }
        return list;
    }

    private static void EnsureKeyReflection(object provider)
    {
        if (_keyGetKeysOnTable != null) return;
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var t = provider.GetType();
        // BcShape.FindMethod, not Type.GetMethod: a name-only lookup hands back the
        // most-derived declaration when BC `new`-hides a member and drives the wrong one
        // silently; FindMethod refuses on ambiguity and answers null on absence (#3069).
        const string Surface = "key-virtual-table";
        const string Detail = "BC shape changed; see #4147";
        var getKeysOnTable = BcShape.FindMethod(t, "GetKeysOnTable", inst, Surface, "GetKeysOnTable", Detail);
        var nclMetadata = t.GetProperty("NclMetadata", inst);
        var rangeType = getKeysOnTable?.GetParameters()[2].ParameterType;
        var getBounds = rangeType == null ? null
            : BcShape.FindMethod(rangeType, "GetInclusiveIntegerBounds", inst, Surface, "GetInclusiveIntegerBounds", Detail);
        var tryGet = nclMetadata?.PropertyType.GetMethods(inst).FirstOrDefault(m =>
            m.Name == "TryGetMetaTableById" && m.GetParameters().Length == 4
            && m.GetParameters()[0].ParameterType == typeof(int));
        var bufferType = getKeysOnTable?.ReturnType.IsGenericType == true
            ? getKeysOnTable.ReturnType.GetGenericArguments()[0]
            : null;
        if (getKeysOnTable == null || nclMetadata == null || getBounds == null || tryGet == null || bufferType == null)
            // BcShapeGapException, not InvalidOperationException: NavMethodScope_AssertError
            // rethrows only this type, so an `asserterror` around a driver hitting this refusal
            // would otherwise SWALLOW it and PASS — inverting the result rather than hiding it
            // (#2946).
            throw new BcShapeGapException(
                Surface, "KeyDataProvider",
                "BC does not expose the shape the Key virtual-table rewrite drives "
                + "(GetKeysOnTable, NclMetadata, Range.GetInclusiveIntegerBounds, "
                + "NCLMetadata.TryGetMetaTableById(int, out, bool, int)) — see #4147");
        _keyBufferType = bufferType;
        _keyGetKeysOnTable = getKeysOnTable;
        _keyNclMetadata = nclMetadata;
        _keyGetBounds = getBounds;
        _keyTryGetMetaTable = tryGet;
    }
}
