// RecordPatches.CreateObjectInstance.cs — concrete-type-aware NCLMetaTable.CreateObjectInstance.
//
// Why this exists
// ---------------
// NavRecord.OldRecord (the xRec before-image) is built lazily. BC's design: when the
// owner record is a concrete subclass (e.g. Record23 for table Vendor), build the
// before-image via `metaTable.CreateObjectInstance(...)` so it is the SAME concrete type.
// The compiled AL accessor for xRec is a cast to that concrete type:
//
//     private Record23 xRec => (Record23)(object)((NavRecord)this).OldRecord;
//
// The runner forces NCLMetaApplicationObject.get_ApplicationObjectConstructor to null
// (the real getter NREs on a skeleton meta), so the original CreateObjectInstance falls
// into its `new NavRecord(...)` fallback and returns a BASE NavRecord. Reading xRec then
// throws `InvalidCastException: Unable to cast 'NavRecord' to 'Record23'`.
//
// This replacement reproduces CreateObjectInstance faithfully, but in the
// no-constructor-delegate case it builds the table's concrete Record{Id} CLR type
// (resolved from the loaded app assemblies) instead of a base NavRecord — which is
// exactly what the real ApplicationObjectConstructor delegate would have produced.
// Falls back to a base NavRecord only when no concrete Record{Id} type exists (virtual /
// system tables), preserving prior behaviour for those.
//
// Runtime-engine layer (Ncl.dll) — allowed to modify (see precompiled-dll-respect.md).
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Runtime.Extensions;

namespace AlRunnerV2.Patches;

public static partial class RecordPatches
{
    // Cached ctors: concrete Record{Id} 6-arg ctor (per type) and the base NavRecord 7-arg ctor.
    private static readonly ConcurrentDictionary<Type, ConstructorInfo?> _concreteRecordCtors = new();
    private static ConstructorInfo? _baseNavRecordCtor;
    private static FieldInfo? _fOrderedExtensionObjects;
    private static MethodInfo? _mCreateExtensionInstanceAndBindToParent;
    private static bool _extBindResolved;

    /// <summary>
    /// Replacement for NCLMetaTable.CreateObjectInstance(ITreeObject, bool, NavRecord, string, SecurityFiltering).
    /// Builds the concrete Record{Id} type (so xRec / OldRecord casts succeed) and binds
    /// any table extensions, mirroring the original method's behaviour.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NavRecord? NCLMetaTable_CreateObjectInstance(
        object metaTableSelf,
        ITreeObject? parent,
        bool isTemporary,
        NavRecord? sharedTable,
        string companyName,
        object securityFiltering)
    {
        int tableId = GetTableId(metaTableSelf);
        Type? concrete = tableId > 0 ? FindRecordType(tableId) : null;

        NavRecord? rec;
        if (concrete != null && concrete != typeof(NavRecord))
        {
            // Concrete Record{Id} ctor: (ITreeObject parent, NCLMetaTable metaTable, bool isTemporary,
            //                            NavRecord sharedTable, string companyName, SecurityFiltering securityFiltering)
            var ctor = _concreteRecordCtors.GetOrAdd(concrete,
                t => Array.Find(t.GetConstructors(), c => c.GetParameters().Length == 6));
            rec = ctor != null
                ? (NavRecord?)ctor.Invoke(new object?[] { parent, metaTableSelf, isTemporary, sharedTable, companyName, securityFiltering })
                : BuildBaseNavRecord(metaTableSelf, parent, tableId, isTemporary, sharedTable, companyName, securityFiltering);
        }
        else
        {
            rec = BuildBaseNavRecord(metaTableSelf, parent, tableId, isTemporary, sharedTable, companyName, securityFiltering);
        }

        if (rec != null)
        {
            BindTableExtensions(metaTableSelf, rec);
            RegisterParsedTableExtensions(rec, tableId);
            // Wire field OnValidate/OnLookup handlers + field-validate subscribers onto this table's
            // (now built+cached) metatable — covers tables built on demand at runtime (e.g. a
            // precompiled BaseApp table) that the startup passes missed. Safe here (outside GetOrAdd's
            // value factory, so any metadata lookups hit the cache, no reentrancy).
            WireFieldTriggerHandlersForTable(tableId, metaTableSelf);
            EventSubscriberPatches.InjectValidateSubsForTable(tableId, metaTableSelf);
        }
        return rec;
    }

    private static NavRecord? BuildBaseNavRecord(
        object metaTableSelf, ITreeObject? parent, int tableId, bool isTemporary,
        NavRecord? sharedTable, string companyName, object securityFiltering)
    {
        // Base NavRecord ctor: (ITreeObject, int tableId, NCLMetaTable, bool, NavRecord, string, SecurityFiltering)
        _baseNavRecordCtor ??= Array.Find(typeof(NavRecord).GetConstructors(),
            c => c.GetParameters().Length == 7 && c.GetParameters()[1].ParameterType == typeof(int));
        return (NavRecord?)_baseNavRecordCtor?.Invoke(
            new object?[] { parent, tableId, metaTableSelf, isTemporary, sharedTable, companyName, securityFiltering });
    }

    private static int GetTableId(object metaTableSelf)
    {
        try
        {
            var p = metaTableSelf.GetType().GetProperty("TableId", BindingFlags.Public | BindingFlags.Instance);
            if (p != null) { var v = p.GetValue(metaTableSelf); if (v is int id) return id; }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Mirrors CreateObjectInstance's `foreach (ext in orderedExtensionObjects) ext.CreateExtensionInstanceAndBindToParent(rec)`.
    /// </summary>
    private static void BindTableExtensions(object metaTableSelf, NavRecord rec)
    {
        // Resolve the private orderedExtensionObjects field (declared on NCLMetaTable; walk up just in case).
        if (_fOrderedExtensionObjects == null)
        {
            for (var t = metaTableSelf.GetType(); t != null && _fOrderedExtensionObjects == null; t = t.BaseType)
                _fOrderedExtensionObjects = t.GetField("orderedExtensionObjects",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        }
        var exts = _fOrderedExtensionObjects?.GetValue(metaTableSelf) as IEnumerable;
        if (exts == null) return;

        foreach (var ext in exts)
        {
            if (ext == null) continue;
            if (!_extBindResolved)
            {
                _extBindResolved = true;
                _mCreateExtensionInstanceAndBindToParent = ext.GetType().GetMethod(
                    "CreateExtensionInstanceAndBindToParent",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: new[] { typeof(NavRecord) }, modifiers: null);
            }
            if (_mCreateExtensionInstanceAndBindToParent == null)
                throw new InvalidOperationException(
                    "NCLMetaTable_CreateObjectInstance: table has extension objects but " +
                    "CreateExtensionInstanceAndBindToParent(NavRecord) could not be resolved on " +
                    $"{ext.GetType().FullName} — table-extension fields on this record would be missing.");
            _mCreateExtensionInstanceAndBindToParent.Invoke(ext, new object?[] { rec });
        }
    }

    // tableextension object id → emitted "TableExtension{id}" CLR type (subclass of
    // NavRecordExtension). Cached HITS only, mirroring _recordTypeCache: a miss can become a
    // hit once the test assembly loads, so misses fall through to the scan.
    private static readonly ConcurrentDictionary<int, Type> _tableExtensionTypeCache = new();

    internal static Type? FindTableExtensionType(int extId)
    {
        if (_tableExtensionTypeCache.TryGetValue(extId, out var cached)) return cached;
        var name = $"TableExtension{extId}";
        var preferred = BcRuntime.CurrentTestAssembly;
        if (preferred != null)
        {
            var hit = FindTableExtensionTypeIn(preferred, name);
            if (hit != null) { _tableExtensionTypeCache[extId] = hit; return hit; }
        }
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm == preferred) continue;
            var hit = FindTableExtensionTypeIn(asm, name);
            if (hit != null) { _tableExtensionTypeCache[extId] = hit; return hit; }
        }
        return null;
    }

    private static Type? FindTableExtensionTypeIn(Assembly asm, string name)
    {
        try
        {
            return Array.Find(asm.GetTypes(),
                x => x.Name == name && typeof(NavRecordExtension).IsAssignableFrom(x));
        }
        catch { return null; }
    }

    /// <summary>
    /// Instantiate each tableextension declared for <paramref name="tableId"/>'s base table and
    /// register it on the record via NavRecord.RegisterTableExtension — populating
    /// orderedTableExtensions, which is what NavRecord's Insert/Modify/Delete/Rename pipeline
    /// (ext.OnBeforeInsert/OnInsert/OnAfterInsert …) and InvokeFieldTriggerHandler (which finds
    /// the extension instance by handler.HandlerType) read.
    ///
    /// Our hand-built NCLMetaTable has an empty orderedExtensionObjects, so BC's own
    /// NCLTableExtension.CreateExtensionInstanceAndBindToParent path (BindTableExtensions above)
    /// never fires; this is the runner-side equivalent. Runtime-engine layer — allowed.
    /// </summary>
    internal static void RegisterParsedTableExtensions(NavRecord rec, int tableId)
    {
        if (tableId <= 0) return;
        if (!_parsedTables.TryGetValue(tableId, out var parsed)) return;
        if (!_extensionIdsByBaseTable.TryGetValue(parsed.TableName.ToLowerInvariant(), out var extIds)
            || extIds.Count == 0)
            return;

        var already = rec.OrderedTableExtensions;
        foreach (var extId in extIds)
        {
            var extType = FindTableExtensionType(extId);
            if (extType == null) continue;
            // Idempotent: a record instance must carry at most one extension of each type
            // (double-registration would fire every extension trigger twice).
            bool present = false;
            for (int i = 0; i < already.Count; i++)
                if (already[i].GetType() == extType) { present = true; break; }
            if (present) continue;

            // Emitted ctor: TableExtension{id}(ITreeObject parent) : base(parent, id, null).
            var ctor = extType.GetConstructor(new[] { typeof(ITreeObject) });
            if (ctor == null)
                throw new InvalidOperationException(
                    $"{extType.FullName} has no (ITreeObject) constructor — cannot register table extension " +
                    $"for table {tableId}; its triggers would silently not fire.");
            var ext = (NavRecordExtension?)ctor.Invoke(new object?[] { rec });
            if (ext != null)
                rec.RegisterTableExtension(ext);
        }
    }
}
