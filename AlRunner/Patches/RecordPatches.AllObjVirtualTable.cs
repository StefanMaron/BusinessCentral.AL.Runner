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
    internal static RunnerOutOfScopeException AllObjShapeGap(string detail)
        => VirtualTableShapeGap("AllObj (virtual table 2000000038)", "allobj-virtual-table", detail);

    internal const int AllObjVirtualTableId = 2000000038;

    private const int AllObjFieldObjectType = 1;
    private const int AllObjFieldObjectId = 3;
    private const int AllObjFieldObjectName = 4;
    // #2963: the two package columns System Application ownership checks compare against the
    // matching Published Application row. Located by field NO here for the same reason the
    // three above are — an AllObj shape change should miss loudly, not write a GUID into
    // whatever column happens to sit at that index.
    // (60 / 61, verified against this BC artifact's own Field metadata for 2000000038 —
    // they are NOT adjacent to the low-numbered columns above.)
    private const int AllObjFieldAppPackageId = 60;
    private const int AllObjFieldAppRuntimePackageId = 61;

    private static bool _aovReflectionReady;
    // #3015 — AllObj's own columns, checked once per process. Separate from
    // _aovReflectionReady on purpose: that flag guards work driven by ANY populator's
    // metatable, this one guards a question that can only be asked of AllObj's.
    private static bool _aovColumnsChecked;
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
        EnsureAllObjColumnsExist(allObjMetaTable);
        EnsureAllObjReflection(allObjMetaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw AllObjShapeGap("AllObj data access has no in-memory provider");

        var ordinals = EnsureAllObjObjectTypeOrdinals(allObjMetaTable);
        var done = _aovPopulatedByProvider.GetValue(provider, static _ => new ConcurrentDictionary<(int, int), byte>());

        // #3117: built on FIRST ACTUAL INSERT, not on entry. PopulateAllObjVirtualTable runs on
        // every AllObj data-access handout, but `done` makes all but the first few handouts
        // insert nothing — and BuildObjectOwnerIndex walks every registered module assembly's
        // TypeDef name index six times (once per _emittedObjectTypePrefixes entry), Base
        // Application included, so an eager build paid that price to produce no rows.
        //
        // Measured on the al-language corpus (2665 tests, BC 28.1, warm compile cache), which
        // is what settles the "cost is not the reason for a skip any more" claim #3107's PR
        // body made with no number behind it:
        //
        //   eager (as merged in #3107): 74 calls, 74 builds, 1905.2 ms total
        //   this shape:                 74 calls,  3 builds,   48.7 ms total
        //
        // 71 of the 74 handouts rebuilt a 10,349-entry dictionary and inserted zero rows. For
        // scale, the rest of this method — EnumerateKnownAlObjects plus the inserts, which is
        // work that actually has to happen — measured 1119.5 ms over the same 74 calls, so the
        // redundant build was NOT the same order as the necessary work: it was 1.7x larger.
        Dictionary<(string Kind, int Id), Guid>? ownerIndex = null;

        foreach (var (kind, id, name, _) in EnumerateKnownAlObjects())
        {   // AllObj has no caption column; the shared inventory carries one for
            // AllObjWithCaption (2000000058), which reads the same rows.
            if (id <= 0) continue;
            var normalized = NormalizeObjectTypeName(kind);
            if (!ordinals.TryGetValue(normalized, out var typeOrdinal))
                // This AL object kind has no column in THIS BC version's AllObj option
                // set (e.g. a kind introduced after the artifact). Real BC would not
                // list it either — skipping is faithful, inventing an ordinal is not.
                continue;
            if (!done.TryAdd((typeOrdinal, id), 0))
                continue;
            // The app that owns this object: stated by the declaring .app's own
            // SymbolReference.json when it came from one, or by the emitted assembly that
            // declares it when the runner compiled it here. Anything the index cannot answer
            // for stays Guid.Empty and therefore matches no Published Application row.
            //
            // NOT a fallback to the current bundle. That is the conservative direction and it
            // is deliberate: an object kind with no entry in _emittedObjectTypePrefixes, or one
            // reached before its assembly was registered, is an object whose owner the runner
            // does not know — and answering "the bundle" there would let the bundle own it,
            // which is a permission granted on a guess. An unowned object simply fails the
            // ownership check, which is what a wrong guess should look like.
            ownerIndex ??= BuildObjectOwnerIndex();
            var owningAppId = ownerIndex.TryGetValue((normalized, id), out var owner) ? owner : Guid.Empty;
            InsertAllObjRow(provider, allObjMetaTable, typeOrdinal, id, name, owningAppId);
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
        // Pages and pageextensions live in separate dictionaries because AL gives them
        // separate id namespaces (#1710) — both are enumerated, so an app declaring
        // `page N` and `pageextension N` reports BOTH rows instead of only whichever
        // one was parsed last.
        foreach (var p in _parsedPages.Values)
            yield return ("Page", p.Id, p.Name, SourceCaptionFor("Page", p.Id));
        foreach (var p in _parsedPageExtensions.Values)
            yield return ("PageExtension", p.Id, p.Name, SourceCaptionFor("PageExtension", p.Id));
        foreach (var r in _parsedReports.Values)
            // SourceCaptionFor("Report", …) reads r.Caption itself — AlReportParser is the
            // only pass that parses a report's Caption (#1714). Going through the same
            // accessor as every other kind is what keeps that single source uniform.
            yield return ("Report", r.Id, r.Name, SourceCaptionFor("Report", r.Id));
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
    private static void InsertAllObjRow(object provider, NCLMetaTable allObjMetaTable, int typeOrdinal, int objectId, string objectName, Guid owningAppId)
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
                // #2963 — the owning app's package ids, the same deterministic values
                // AppPackageIdentity puts on that app's Published Application row. System
                // Application ownership checks compare the two
                // (Reten. Pol. Allowed Tbl. Impl.ModuleOwnsTable:
                //  `AllObj."App Runtime Package ID" <> PublishedApplication."Runtime Package ID"`),
                // so leaving BOTH sides at the type default would answer "yes, this app owns
                // it" for every app/table pair rather than for the right one. An object whose
                // owner is unknown keeps Guid.Empty and therefore matches nothing.
                AllObjFieldAppPackageId
                    => NavValue.CreateNavValueFromObject(field, AppPackageIdentity.PackageIdFor(owningAppId)),
                AllObjFieldAppRuntimePackageId
                    => NavValue.CreateNavValueFromObject(field, AppPackageIdentity.RuntimePackageIdFor(owningAppId)),
                // Every other AllObj column (Object Namespace, …) is exactly what
                // AllObjDataProvider emits for a base object with no namespace: the type's
                // default value.
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
            ?? throw AllObjShapeGap(
                "AllObj metatable has no field 1 (\"Object Type\") "
                + $"[tableId={allObjMetaTable.TableId} name='{allObjMetaTable.TableName}' "
                + $"allFields={(allFields == null ? "null" : string.Join("/", allFields.Select(f => f.FieldNo)))}]");

        var optionMetadata = typeField.FieldOptionMetadata
            ?? throw AllObjShapeGap(
                "AllObj \"Object Type\" carries no option metadata, so its ordinals cannot be resolved");

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
            throw AllObjShapeGap($"AllObj \"Object Type\" option string is empty ('{optionString}')");

        if (Environment.GetEnvironmentVariable("ALRUNNER_ALLOBJ_TRACE") == "1")
            Console.Error.WriteLine("[RecordPatches] AllObj Object Type OptionString = '" + optionString + "' → "
                + string.Join(", ", map.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));

        _aovObjectTypeOrdinals = map;
        return map;
    }

    /// <summary>
    /// Owning app id per (normalized object kind, object id), for every object that came from
    /// a registered precompiled dependency .app. The app id is the one stated at the root of
    /// that .app's own SymbolReference.json — the same source
    /// <c>RecordPatches.MetadataPermissionSetVirtualTable</c> uses for permission-set
    /// ownership, so the two cannot disagree about who owns what.
    ///
    /// Objects the runner COMPILED in this process are indexed too, from the emitted assembly
    /// that declares them, so the bundle under test and every source-compiled dependency each
    /// own exactly their own objects.
    ///
    /// Anything this index cannot answer for is left OUT, and the caller then uses
    /// <c>Guid.Empty</c> rather than falling back to whichever app is current. That is
    /// deliberate and it fails closed: an unowned object matches no Published Application row,
    /// so an ownership check on it declines. Attributing it to the current bundle instead would
    /// grant a permission on a guess.
    /// </summary>
    private static Dictionary<(string Kind, int Id), Guid> BuildObjectOwnerIndex()
    {
        var index = new Dictionary<(string Kind, int Id), Guid>();
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            BcAppSymbolCache.AppSymbols symbols;
            // #3117: NOT swallowed. This used to `catch { continue; }` on the reasoning that
            // "the AllObj row itself is built either way" — true, but it is then built with
            // Guid.Empty in the two package columns, and an ownership check reading those
            // cannot tell "this app does not own it" from "we could not find out". That is the
            // silent default .claude/rules/loud-failures.md exists to prevent, and it is the
            // same reasoning AllObjShapeGap already applies to an unreadable option ordinal:
            // the owner is a STORED COLUMN VALUE, so a wrong one mis-keys every row it writes
            // and no test can see it.
            try { symbols = BcAppSymbolCache.Get(appPath); }
            catch (Exception ex)
            {
                throw AllObjShapeGap(
                    $"the SymbolReference of dependency package '{Path.GetFileName(appPath)}' could not be "
                    + $"read ({ex.GetType().Name}: {ex.Message}), so every object it declares would be "
                    + "stamped with no owning app instead of its real one");
            }
            if (!Guid.TryParse(symbols.AppId, out var appId) || appId == Guid.Empty)
                // A symbol reference stating no app id leaves its objects unowned rather than
                // getting an invented owner — Guid.Empty then matches no Published Application
                // row, which is the honest answer.
                continue;
            foreach (var o in symbols.Objects)
                index[(NormalizeObjectTypeName(o.Kind), o.Id)] = appId;
        }

        // Objects the runner COMPILED in this process — the bundle under test and any
        // source-compiled dependency — have no .app symbol reference to be owned by, so their
        // owner is the app whose emitted assembly declares them.
        //
        // "Whichever bundle is current" is NOT a substitute, and this is measured rather than
        // guessed: with that fallback, running the whole runner-extras tree in one process
        // stamped this suite's own table with a DIFFERENT app group's runtime package id
        // (expected {6EDA7750-…}, got {5A8EDAC8-…}), because AllObj had been populated while
        // another app group was current. Reading the declaring assembly is exact regardless of
        // which app group is executing.
        //
        // ── Why this is a UNION and not an either/or (#3049) ──────────────────────────────
        // This loop used to skip every assembly whose app id had already been seen in some
        // registered .app's SymbolReference.json, on the assumption that a symbol reference
        // lists all of its app's objects, so re-deriving them from the assembly was pure cost.
        // That assumption does not hold for the app UNDER TEST. RegisterBundleSymbolApps
        // deliberately registers a prebuilt `.app` sitting in the bundle root — it is where a
        // bundle's own BC-compiler-assigned query column ids come from — while the runner
        // compiles that same app from SOURCE. A bundle-root .app that predates a source change
        // is therefore normal, not corrupt, and every object added since is missing from it.
        //
        // Measured on the al-language corpus: its checked-in
        // `AL Language_AL Language Coverage Tests_1.0.0.0.app` lists 191 objects and has not
        // been rebuilt since corpus PR #7, so table 60404 "ALT Reten Pol Owned" — compiled from
        // source in this process — was stamped Guid.Empty. Codeunit 60405's
        // AppCanRegisterItsOwnTableOnTheAllowedList then failed on real System Application
        // code: Reten. Pol. Allowed Tbl. Impl.ModuleOwnsTable compares that column against the
        // caller's Published Application row and declined the app its own table. The sibling
        // negative kept passing throughout, because "unowned" and "owned by someone else" are
        // the same answer to that comparison.
        //
        // So both sources are read and the SYMBOL answer wins on conflict: it is exact for a
        // precompiled .app, and the assembly pass only fills ids no symbol reference claimed.
        //
        // On cost (#3117): #3107 asserted "cost is not the reason for a skip any more" with no
        // number behind it. Measured since, on the al-language corpus: one build of this index
        // is ~25 ms with Base Application loaded (10,349 entries), and it is NOT free. What
        // makes it affordable is that the caller now builds it at most once per handout that
        // actually inserts a row — see PopulateAllObjVirtualTable — rather than on every
        // handout. The half of the original claim that does hold is the mechanism:
        // TypeNamesWithPrefix reads the TypeDef table and resolves no CLR Type at all, where
        // EnumerateWithPrefix materialised a RuntimeType per match.
        foreach (var (asm, appId) in AlRunner.BcRuntime.RegisteredModuleAssemblies())
        {
            var asmName = SafeAssemblyName(asm);
            AlRunner.Infrastructure.AssemblyTypeIndex typeIndex;
            // #3117: NOT swallowed, for the reason on the symbol-reference read above. A throw
            // here drops EVERY object this assembly declares from the index, and each one then
            // takes the Guid.Empty branch in PopulateAllObjVirtualTable — silently, with no
            // message and no exit-code change. AssemblyTypeIndex's own constructor is written
            // to FALL BACK rather than throw (unreadable metadata degrades to
            // Assembly.GetTypes()), so an exception escaping For() is genuinely exceptional
            // rather than routine: measured over the al-language corpus (2665 tests) and
            // tests/runner-extras (304 tests), neither this site nor the two around it fired
            // once.
            try { typeIndex = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm); }
            catch (Exception ex)
            {
                throw AllObjShapeGap(
                    $"the type index of module assembly '{asmName}' could not be read "
                    + $"({ex.GetType().Name}: {ex.Message}), so every object it declares would be "
                    + "stamped with no owning app instead of its real one");
            }

            AddEmittedAssemblyOwners(index, asmName, appId, prefix =>
                typeIndex.IsMetadataBacked
                    ? typeIndex.TypeNamesWithPrefix(prefix)
                    // A dynamic (Reflection.Emit) assembly has no TypeDef table to read;
                    // it has a handful of types, so resolving them is cheap.
                    : typeIndex.EnumerateWithPrefix(prefix).Select(t => t.Name));
        }
        return index;
    }

    /// <summary>Assembly name for a diagnostic, without letting the diagnostic itself throw.</summary>
    private static string SafeAssemblyName(System.Reflection.Assembly asm)
    {
        try { return asm.GetName().Name ?? asm.ToString(); }
        catch { return "<unnamed assembly>"; }
    }

    /// <summary>
    /// Fold one registered module assembly's emitted AL object types into
    /// <paramref name="index"/>, one pass per <see cref="_emittedObjectTypePrefixes"/> entry.
    ///
    /// <para><paramref name="typeNamesForPrefix"/> is a parameter rather than a direct
    /// <c>AssemblyTypeIndex</c> call so the FAILURE path is directly testable — the whole point
    /// of #3117 is that this method must not answer quietly when it cannot read, and a test
    /// cannot make a real R2R assembly's TypeDef table unreadable on demand. Same shape as
    /// <c>Win32Stubs.FindCompiler(Func&lt;string, bool&gt;)</c>, pinned by
    /// <c>Win32StubsLoudFailureTests</c> for the same reason.</para>
    /// </summary>
    internal static void AddEmittedAssemblyOwners(
        Dictionary<(string Kind, int Id), Guid> index,
        string assemblyName,
        Guid appId,
        Func<string, IEnumerable<string>> typeNamesForPrefix)
    {
        foreach (var (prefix, kind) in _emittedObjectTypePrefixes)
        {
            var normalizedKind = NormalizeObjectTypeName(kind);
            // The enumeration runs INSIDE the try, not just the call that produces it. Both
            // producers are lazy — TypeNamesWithPrefix is a `yield return` iterator and
            // EnumerateWithPrefix(...).Select(...) is deferred too — so wrapping only the
            // assignment (as the code this replaces did) caught nothing the enumeration itself
            // raised; TypeNamesWithPrefix's own "metadata-only" InvalidOperationException would
            // have sailed straight past it.
            try
            {
                foreach (var name in typeNamesForPrefix(prefix))
                {
                    if (!int.TryParse(name.AsSpan(prefix.Length), out var id) || id <= 0) continue;
                    // TryAdd, not an assignment: a symbol reference that named this object
                    // already answered, and that answer is the authoritative one (#3049).
                    index.TryAdd((normalizedKind, id), appId);
                }
            }
            catch (RunnerOutOfScopeException)
            {
                throw;   // already named its surface; do not re-wrap
            }
            catch (Exception ex)
            {
                throw AllObjShapeGap(
                    $"module assembly '{assemblyName}' could not be scanned for '{prefix}*' object "
                    + $"types ({ex.GetType().Name}: {ex.Message}), so every {kind} it declares would "
                    + "be stamped with no owning app instead of its real one");
            }
        }
    }

    /// <summary>
    /// CLR type-name prefix → AL object kind, for the object kinds an emitted AL assembly
    /// declares as a type named <c>&lt;Prefix&gt;&lt;N&gt;</c>. The same mapping
    /// <c>BcRuntime</c>'s publisher-scope decode uses, kept in the AllObj kind vocabulary
    /// (<c>Record</c> is a Table) because <see cref="NormalizeObjectTypeName"/> is applied to
    /// both sides.
    /// </summary>
    private static readonly (string Prefix, string Kind)[] _emittedObjectTypePrefixes =
    {
        ("Record", "Table"),
        ("Codeunit", "Codeunit"),
        ("Page", "Page"),
        ("Report", "Report"),
        ("Query", "Query"),
        ("XmlPort", "XMLport"),
    };

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

    /// <summary>
    /// Bind the shared runtime-engine reflection every virtual-table populator uses. Despite
    /// the "AllObj" in the name this is NOT AllObj-specific and NOT AllObj-only: eighteen
    /// populators call it and seventeen of them hand it their OWN metatable — Windows Language,
    /// Time Zone, Feature Key, Codeunit Metadata and the rest. Only
    /// <see cref="PopulateAllObjVirtualTable"/> passes AllObj's.
    ///
    /// So <paramref name="anyMetaTable"/> is used for exactly one thing: reaching the Ncl
    /// assembly it was loaded from. Nothing here may read its table id, its fields or its
    /// options, and NOTHING THAT VALIDATES A PARTICULAR TABLE'S SHAPE BELONGS IN THIS METHOD.
    /// #3015 put AllObj's field-number check here and it refused Windows Language (2000000045)
    /// for not having AllObj's columns — a loud WRONG refusal on a surface that works, which is
    /// the same defect class as the silent wrong answer that change exists to remove. Worse, it
    /// threw before <c>_aovReflectionReady</c> was set, so which populator reached this method
    /// first decided whether the process worked at all; six of eight CI legs passed on that
    /// ordering luck. The check now lives in
    /// <see cref="EnsureAllObjColumnsExist(NCLMetaTable)"/>, called from the one site that
    /// holds the genuine metatable.
    /// </summary>
    private static void EnsureAllObjReflection(NCLMetaTable anyMetaTable)
    {
        if (_aovReflectionReady) return;

        var nclAsm = anyMetaTable.GetType().Assembly;
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

    /// <summary>
    /// The same defect class as AlRunner#3015, at the one seeder that resolves its columns by
    /// field NUMBER rather than by name. <see cref="InsertAllObjRow"/> switches on
    /// <c>field.FieldNo</c> while walking the metatable, so a number that matches nothing is
    /// simply never written: the row still inserts, the column keeps BC's own default, and the
    /// ownership comparison
    /// <c>AllObj."App Runtime Package ID" &lt;&gt; PublishedApplication."Runtime Package ID"</c>
    /// then declines for every app while BC logs a warning rather than raising.
    ///
    /// That is not hypothetical here — #3004 shipped 6/7 for the two package columns, which are
    /// 60/61, and the stamp silently did nothing. It was found by checking, not by a failure.
    ///
    /// CALLED FROM <see cref="PopulateAllObjVirtualTable"/> AND NOWHERE ELSE — that is the one
    /// call site holding the genuine AllObj metatable. The first version of this lived in
    /// <see cref="EnsureAllObjReflection"/>, whose parameter is named for AllObj and is not
    /// AllObj for seventeen of its eighteen callers; it duly refused Windows Language
    /// (2000000045) for not declaring AllObj's columns. The table-id check below exists so that
    /// a future re-wiring says "wrong table" instead of inventing a shape gap in a table that
    /// has none.
    ///
    /// Checked once per process, never per row: AllObj is seeded one row per known AL object,
    /// thousands of them, and a per-row check would be paid thousands of times over to answer a
    /// question about the table.
    /// </summary>
    private static void EnsureAllObjColumnsExist(NCLMetaTable allObjMetaTable)
    {
        if (_aovColumnsChecked) return;

        // A claim about the RUNNER's own wiring, not about BC's metadata, so it is deliberately
        // not an AllObjShapeGap: a shape gap says "this BC artifact is not the shape we were
        // written against", which would be a lie about whatever table was actually passed.
        if (allObjMetaTable.TableId != AllObjVirtualTableId)
            throw new InvalidOperationException(
                $"EnsureAllObjColumnsExist was handed table {allObjMetaTable.TableId} "
                + $"'{allObjMetaTable.TableName}', not AllObj ({AllObjVirtualTableId}). It may only "
                + "be called from PopulateAllObjVirtualTable — see AlRunner#3015.");

        var byNo = new HashSet<int>();
        foreach (var f in GetAllFields(allObjMetaTable) ?? Enumerable.Empty<NCLMetaField>())
            byNo.Add(f.FieldNo);

        var required = new (int No, string Name)[]
        {
            (AllObjFieldObjectType, "Object Type"),
            (AllObjFieldObjectId, "Object ID"),
            (AllObjFieldObjectName, "Object Name"),
            (AllObjFieldAppPackageId, "App Package ID"),
            (AllObjFieldAppRuntimePackageId, "App Runtime Package ID"),
        };
        var missing = required.Where(r => !byNo.Contains(r.No)).ToList();
        if (missing.Count == 0)
        {
            // Set AFTER the work, never before it, so a refusal keeps refusing rather than
            // latching a "checked" that was never reached.
            _aovColumnsChecked = true;
            return;
        }

        throw AllObjShapeGap(
            "AllObj metatable has no field "
            + string.Join(", ", missing.Select(m => $"{m.No} (\"{m.Name}\")"))
            + " — every AllObj row would be written with BC's own default in that column and "
            + "every later read would look correct; module-ownership checks would then decline "
            + $"for every app without raising [tableId={allObjMetaTable.TableId} "
            + $"name='{allObjMetaTable.TableName}' "
            + $"fields={string.Join("/", byNo.OrderBy(n => n))}] — see AlRunner#3015");
    }
}
