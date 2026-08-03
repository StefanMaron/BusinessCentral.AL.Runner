// RecordPatches.AllObjVirtualTable — managed provider for the AllObj system
// virtual table (2000000038).
//
// WHY THIS EXISTS
//   On the real service tier AllObj is a VIRTUAL table: its rows are computed on
//   the fly by Microsoft.Dynamics.Nav.Runtime.AllObjDataProvider from
//   NCLMetadata.GetSnapshotOfAllObjects() — one row per (ObjectType, ObjectId)
//   the tenant has. There are no stored rows.
//
//   Our runtime routes every table's data access through
//   NavDataAccessSource_GetDataAccessForTable → an in-memory TempTableDataProvider,
//   and for 2000000038 that store was empty. Worse, BC's own back-end for AllObj
//   is unusable here: NCLMetadata.GetSnapshotOfAllObjects is Cecil-replaced with
//   `return new SortedList<...>()` (the real body locks a syncRoot that is null on
//   a GetUninitializedObject NCLMetadata and reads the System App resource we do
//   not have) — see NclCecilRewrite.cs. So both halves were empty and
//   `AllObj.Get(<type>, <id>)` returned FALSE for every object, including objects
//   the runner compiled itself moments earlier.
//
//   AL that gates on object existence via AllObj is a normal pattern and silently
//   took its not-found branch. Pageworks raises 'reportNotFound: Report N does not
//   exist or you do not have permission to read it' on exactly that basis.
//
// WHAT THIS DOES (faithful, managed, R2R-safe)
//   We keep the in-memory TempTableDataProvider (so BC's own filter/sort/Find
//   engine runs over the rows and applies whatever AL filters the test set — the
//   same engine every other table uses) and POPULATE it with one row per object
//   the runner actually knows about. Nothing is fabricated: an object appears in
//   AllObj if and only if some runner registry has a real (kind, id, name) for it.
//
//   Row values are laid out exactly as BC's AllObjDataProvider lays them out:
//   VirtualDataProvider.GetSystemPopulatedVirtualRecordValues(metaTable, systemId)
//   — BC's OWN helper — fills the timestamp / SystemId / audit slots, and we then
//   write Object Type / Object ID / Object Name into the slots BC's own
//   NCLMetaField.FieldIndex says they occupy. Every remaining column gets BC's own
//   NavValue.GetDefaultNavValue for that field, which is what AllObjDataProvider
//   itself produces for base objects (App Package ID / App Runtime Package ID are
//   literally `?? Guid.Empty` there, and the namespace column is empty for
//   namespace-less objects).
//
//   The "Object Type" option ordinals are NOT hardcoded — they are read out of the
//   parsed AllObj metatable's own field-1 NCLOptionMetadata.OptionString and
//   matched by NAME, so the mapping tracks whatever the System Application package
//   in the resolved BC artifact declares.
//
// PRECOMPILED-DLL RESPECT
//   No BC business-logic body is touched. VirtualDataProvider, NCLMetaTable,
//   NavValue, ReadOnlyRecordBuffer and TempTableDataProvider are runtime-engine
//   types; we call BC's own helpers by reflection and feed the result into our own
//   in-memory store.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int AllObjVirtualTableId = 2000000038;

    private const int AllObjFieldObjectType = 1;
    private const int AllObjFieldObjectId = 3;
    private const int AllObjFieldObjectName = 4;

    private static bool _aovReflectionReady;
    // Shared by AllObj, Report Metadata and Report Layout List; see SystemPopulatedValues.
    private static SystemPopulatedValues? _aovSystemValues;
    private static ConstructorInfo? _aovCtorReadOnlyBuffer;    // ReadOnlyRecordBuffer(NCLMetaApplicationObject, NavValue[])
    private static ConstructorInfo? _aovCtorMutableBuffer;     // MutableRecordBuffer(ReadOnlyRecordBuffer)
    private static MethodInfo? _aovTtdpInsert;                 // TempTableDataProvider.Insert(int, MutableRecordBuffer, InsertOptions, out ReadOnlyRecordBuffer)
    private static object? _aovInsertOptionsNone;
    private static MethodInfo? _aovNavOptionCreate;            // NavOption.Create(NCLOptionMetadata, int)
    private static MethodInfo? _aovNavIntegerCreate;           // NavInteger.Create(int)
    private static MethodInfo? _aovNavTextCreateTruncated;     // NavText.CreateTruncated(int, string)
    private static MethodInfo? _aovGetDefaultNavValue;         // NavValue.GetDefaultNavValue(INavValueMetadata, bool)

    // Per in-memory-provider guard so repeated data-access handouts within one test
    // only top up objects that appeared since (idempotent, no duplicate-key throws).
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<(int Type, int Id), byte>> _aovPopulatedByProvider = new();

    // Resolved once per process from the parsed AllObj metatable's own option string.
    private static Dictionary<string, int>? _aovObjectTypeOrdinals;

    /// <summary>True if <paramref name="table"/> is the AllObj system virtual table (2000000038).</summary>
    private static bool IsAllObjVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == AllObjVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind the AllObj (2000000038) data access with one
    /// row per object the runner knows about. Idempotent per (provider, objectType, objectId);
    /// called on every 2000000038 data-access handout so objects registered later in the run
    /// still show up.
    /// </summary>
    private static void PopulateAllObjVirtualTable(object dataAccess, NCLMetaTable allObjMetaTable)
    {
        EnsureAllObjReflection(allObjMetaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "AllObj (virtual table 2000000038)",
                "allobj-virtual-table — AllObj data access has no in-memory provider; see docs/scope.md");

        var ordinals = EnsureAllObjObjectTypeOrdinals(allObjMetaTable);
        var done = _aovPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<(int, int), byte>());

        foreach (var (kind, id, name, _) in EnumerateKnownAlObjects())
        {   // AllObj has no caption column; the shared inventory carries one for
            // AllObjWithCaption (2000000058), which reads the same rows.
            if (id <= 0) continue;
            if (!ordinals.TryGetValue(NormalizeObjectTypeName(kind), out var typeOrdinal))
                // This AL object kind has no column in THIS BC version's AllObj option
                // set (e.g. a kind introduced after the artifact). Real BC would not
                // list it either — skipping is faithful, inventing an ordinal is not.
                continue;
            if (!done.TryAdd((typeOrdinal, id), 0))
                continue;
            InsertAllObjRow(provider, allObjMetaTable, typeOrdinal, id, name);
        }
    }

    /// <summary>
    /// Every AL object the runner has a real (kind, id, name) for: source-parsed objects
    /// of the app under test and of any source-compiled dependency, plus objects listed in
    /// the SymbolReference.json of every registered precompiled dependency .app.
    ///
    /// <c>Caption</c> is null when the object declares none — AL's own default caption is
    /// then the object name, applied by the consumer (AllObjWithCaption) so that "not
    /// declared" and "declared as the name" stay distinguishable here. AllObj itself has
    /// no caption column and ignores it.
    /// </summary>
    private static IEnumerable<(string Kind, int Id, string Name, string? Caption)> EnumerateKnownAlObjects()
    {
        foreach (var t in _parsedTables.Values)
            yield return ("Table", t.TableId, t.TableName, SourceCaptionFor("Table", t.TableId));
        foreach (var p in _parsedPages.Values)
        {
            var kind = p.IsExtension ? "PageExtension" : "Page";
            yield return (kind, p.Id, p.Name, SourceCaptionFor(kind, p.Id));
        }
        foreach (var r in _parsedReports.Values)
            yield return ("Report", r.Id, r.Name, r.Caption);
        foreach (var r in _parsedReportExtensions.Values)
            yield return ("ReportExtension", r.Id, r.Name, SourceCaptionFor("ReportExtension", r.Id));
        foreach (var q in _parsedQueries.Values)
        {
            var kind = q.IsExtension ? "QueryExtension" : "Query";
            yield return (kind, q.Id, q.Name, SourceCaptionFor(kind, q.Id));
        }
        foreach (var x in _parsedXmlPorts.Values)
            yield return ("XMLport", x.Id, x.Name, SourceCaptionFor("XMLport", x.Id));
        // Codeunits / enums / *extension kinds — see RecordPatches.AlObjectDeclParser.cs.
        foreach (var d in _parsedObjectDecls.Values)
            yield return (d.Kind, d.Id, d.Name, SourceCaptionFor(d.Kind, d.Id));
        // Enums registered by the emit pipeline and by dependency .app scans.
        foreach (var e in AlEnumMetadataRegistry.Snapshot())
            yield return ("Enum", e.Id, e.Name, SourceCaptionFor("Enum", e.Id));
        // Precompiled dependency .app objects (BaseApp / SystemApp / ISV apps).
        foreach (var o in EnumerateBcAppObjects())
            yield return o;
    }

    private static IEnumerable<(string Kind, int Id, string Name, string? Caption)> EnumerateBcAppObjects()
    {
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            List<BcAppSymbolCache.ObjectSymbol> objects;
            try
            {
                objects = BcAppSymbolCache.Get(appPath).Objects;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[RecordPatches] AllObj: SymbolReference read failed for {Path.GetFileName(appPath)}: {ex.Message}");
                continue;
            }
            foreach (var o in objects)
                yield return (o.Kind, o.Id, o.Name, o.Caption);
        }
    }

    /// <summary>
    /// Build one AllObj row and Insert it into the in-memory provider. Layout mirrors
    /// AllObjDataProvider.GetValuesWithinRangeForKeyField: BC's own
    /// GetSystemPopulatedVirtualRecordValues fills timestamp/SystemId/audit, we write the
    /// three columns we can answer truthfully, and BC's own GetDefaultNavValue fills the rest.
    /// </summary>
    private static void InsertAllObjRow(object provider, NCLMetaTable allObjMetaTable, int typeOrdinal, int objectId, string objectName)
    {
        var values = _aovSystemValues!.Invoke(allObjMetaTable, AllObjVirtualTableId, typeOrdinal, objectId, 0);

        foreach (var field in GetAllFields(allObjMetaTable) ?? Enumerable.Empty<NCLMetaField>())
        {
            var idx = field.FieldIndex;
            if (idx < 0 || idx >= values.Length) continue;
            // Leave the slots BC's own helper already filled (timestamp, SystemId, audit).
            if (values.GetValue(idx) != null) continue;

            object? v = field.FieldNo switch
            {
                AllObjFieldObjectType => _aovNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, typeOrdinal }),
                AllObjFieldObjectId => _aovNavIntegerCreate!.Invoke(null, new object?[] { objectId }),
                AllObjFieldObjectName => _aovNavTextCreateTruncated!.Invoke(null, new object?[] { field.FieldDefinedLength, objectName ?? string.Empty }),
                // Every other AllObj column (App Package ID, App Runtime Package ID,
                // Object Namespace, …) is exactly what AllObjDataProvider emits for a base
                // object with no app package and no namespace: the type's default value.
                _ => _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false }),
            };
            values.SetValue(v, idx);
        }

        var readOnly = _aovCtorReadOnlyBuffer!.Invoke(new object?[] { allObjMetaTable, values });
        var mutable = _aovCtorMutableBuffer!.Invoke(new object?[] { readOnly });
        try
        {
            _aovTtdpInsert!.Invoke(provider, new object?[] { 0, mutable, _aovInsertOptionsNone, null });
        }
        catch (TargetInvocationException tie) when (
            tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
        {
            // Same (Object Type, Object ID) already present — two registries listed the
            // same object. Faithful to a virtual table where the pair is unique.
        }
    }

    /// <summary>
    /// Read the AllObj "Object Type" option ordinals out of the parsed metatable's own
    /// field-1 NCLOptionMetadata.OptionString, keyed by normalized option name. This is
    /// the authority for the mapping — never a hardcoded table.
    /// </summary>
    private static Dictionary<string, int> EnsureAllObjObjectTypeOrdinals(NCLMetaTable allObjMetaTable)
    {
        if (_aovObjectTypeOrdinals != null) return _aovObjectTypeOrdinals;

        var allFields = GetAllFields(allObjMetaTable);
        var typeField = (allFields ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => f.FieldNo == AllObjFieldObjectType)
            ?? throw new RunnerOutOfScopeException(
                "AllObj (virtual table 2000000038)",
                "allobj-virtual-table — AllObj metatable has no field 1 (\"Object Type\") "
                + $"[tableId={allObjMetaTable.TableId} name='{allObjMetaTable.TableName}' "
                + $"allFields={(allFields == null ? "null" : string.Join("/", allFields.Select(f => f.FieldNo)))}]; "
                + "see docs/scope.md");

        var optionMetadata = typeField.FieldOptionMetadata
            ?? throw new RunnerOutOfScopeException(
                "AllObj (virtual table 2000000038)",
                "allobj-virtual-table — AllObj \"Object Type\" carries no option metadata, so its ordinals "
                + "cannot be resolved; see docs/scope.md");

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
                "AllObj (virtual table 2000000038)",
                $"allobj-virtual-table — AllObj \"Object Type\" option string is empty ('{optionString}'); "
                + "see docs/scope.md");

        if (Environment.GetEnvironmentVariable("ALRUNNER_ALLOBJ_TRACE") == "1")
            Console.Error.WriteLine("[RecordPatches] AllObj Object Type OptionString = '" + optionString + "' → "
                + string.Join(", ", map.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));

        _aovObjectTypeOrdinals = map;
        return map;
    }

    private static string NormalizeObjectTypeName(string raw)
    {
        Span<char> buf = stackalloc char[raw.Length];
        int n = 0;
        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == '_') continue;
            buf[n++] = char.ToLowerInvariant(c);
        }
        return new string(buf[..n]);
    }

    private static void EnsureAllObjReflection(NCLMetaTable allObjMetaTable)
    {
        if (_aovReflectionReady) return;

        var nclAsm = allObjMetaTable.GetType().Assembly;
        const string rt = "Microsoft.Dynamics.Nav.Runtime.";

        _aovSystemValues = SystemPopulatedValues.Bind(nclAsm);

        var tReadOnly = nclAsm.GetType(rt + "ReadOnlyRecordBuffer")!;
        var tMetaAppObj = nclAsm.GetType(rt + "NCLMetaApplicationObject")!;
        var tNavValue = nclAsm.GetType(rt + "NavValue")
            ?? ResolveType(rt + "NavValue", "Microsoft.Dynamics.Nav.Types.NavValue")
            ?? throw new InvalidOperationException("NavValue type not found");
        _aovCtorReadOnlyBuffer = tReadOnly.GetConstructor(new[] { tMetaAppObj, tNavValue.MakeArrayType() })
            ?? throw new InvalidOperationException("ReadOnlyRecordBuffer(NCLMetaApplicationObject, NavValue[]) ctor not found");

        var tMutable = nclAsm.GetType(rt + "MutableRecordBuffer")!;
        _aovCtorMutableBuffer = tMutable.GetConstructor(new[] { tReadOnly })
            ?? throw new InvalidOperationException("MutableRecordBuffer(ReadOnlyRecordBuffer) ctor not found");

        var tTtdp = nclAsm.GetType(rt + "TempTableDataProvider")!;
        _aovTtdpInsert = tTtdp.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Insert" && m.GetParameters().Length == 4
                && m.GetParameters()[0].ParameterType == typeof(int))
            ?? throw new InvalidOperationException("TempTableDataProvider.Insert(int,MutableRecordBuffer,InsertOptions,out) not found");
        _aovInsertOptionsNone = Enum.ToObject(nclAsm.GetType(rt + "InsertOptions")!, 0);

        var tOptionMetadata = nclAsm.GetType(rt + "NCLOptionMetadata")
            ?? throw new InvalidOperationException("NCLOptionMetadata type not found");
        var tNavOption = ResolveType(rt + "NavOption", "Microsoft.Dynamics.Nav.Types.NavOption")
            ?? throw new InvalidOperationException("NavOption type not found");
        _aovNavOptionCreate = tNavOption.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { tOptionMetadata, typeof(int) }, modifiers: null)
            ?? throw new InvalidOperationException("NavOption.Create(NCLOptionMetadata,int) not found");

        var tNavInteger = ResolveType(rt + "NavInteger", "Microsoft.Dynamics.Nav.Types.NavInteger")
            ?? throw new InvalidOperationException("NavInteger type not found");
        _aovNavIntegerCreate = tNavInteger.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(int) }, modifiers: null)
            ?? throw new InvalidOperationException("NavInteger.Create(int) not found");

        var tNavText = ResolveType(rt + "NavText", "Microsoft.Dynamics.Nav.Types.NavText")
            ?? throw new InvalidOperationException("NavText type not found");
        _aovNavTextCreateTruncated = tNavText.GetMethod("CreateTruncated", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(int), typeof(string) }, modifiers: null)
            ?? throw new InvalidOperationException("NavText.CreateTruncated(int,string) not found");

        var tNavValueMetadata = nclAsm.GetType(rt + "INavValueMetadata")
            ?? throw new InvalidOperationException("INavValueMetadata type not found");
        _aovGetDefaultNavValue = tNavValue.GetMethod("GetDefaultNavValue",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: new[] { tNavValueMetadata, typeof(bool) }, modifiers: null)
            ?? throw new InvalidOperationException("NavValue.GetDefaultNavValue(INavValueMetadata,bool) not found");

        _aovReflectionReady = true;
    }
}
