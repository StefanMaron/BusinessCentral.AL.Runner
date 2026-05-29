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
            BindTableExtensions(metaTableSelf, rec);
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
}
