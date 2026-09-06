// The permission METADATA layer — issue #2893.
//
// WHAT WAS EMPTY, AND HOW IT WAS MEASURED
//   Probed inside a real run (Base Application closure, BC 28.1.49838.53910):
//   NavAppGroup.BaseGroup — the group the runner plants as the session's OverriddenAppGroup —
//   reports GroupObjectMetadataSummariesCount = 0 and ALL 28 slots of
//   groupObjectMetadataSummariesByType empty. Not permission sets specifically: the runner's
//   app group carries no object metadata summaries at all, because every other object is
//   resolved through the runner's own NCLMetadata cache (RecordPatches.NclMetadataCachePopulator)
//   and nothing ever needed the group's inventory.
//
//   BC's PermissionDataProviderBase.GetMetadataPermissionSets does need it:
//
//       NavAppGroup navAppGroup = NavCurrentThread.ResolveAppGroup();
//       ...navAppGroup.PermissionSetGroupObjectMetadataSummaries...   // 0 entries
//           if (!nclMetadata.TryGetMetaPermissionSetById(id, out var set, false, appGroupId)
//               || set == null)
//               throw new NavMetadataNotFoundException(ObjectType.PermissionSet, id);
//
//   The loop body never runs, so it yields nothing and never reaches the lookup: an empty
//   answer rather than a loud one. Permission (2000000005), Metadata Permission (2000000251)
//   and Expanded Permission (2000000254) are all served by subclasses of that provider.
//
// WHY THIS FILLS ONE SLOT INSTEAD OF BUILDING A GROUP
//   `PermissionSetGroupObjectMetadataSummaries => permissionSetLookup.Value` projects
//   groupObjectMetadataSummariesByType[(int)ObjectType.PermissionSet], and that array is
//   written ONLY by NavAppGroup.CreateGroupObjectMetadataSummaries, from the list handed to
//   the constructor. The slots are FrozenSharingArray<T> — frozen, sorted, structurally
//   shared, no add API. So there are two shapes available, and this file takes the second:
//
//     1. Construct a real NavAppGroup with a summaries list and plant it. Faithful, and its
//        constructor is public — but it replaces the object ~60 BC call sites read
//        (NavCurrentThread, NCLMetadata, NCLMetaTable, PlatformMetadataProvider, the
//        event-subscription layer), so every `GroupId == BaseGroupId` and
//        `OrderedAppCombination.BaseGroupStableCombinationId` identity comparison becomes
//        something to survive. Its only benefit — summaries for all 28 object types — has no
//        consumer today.
//     2. Rebuild the ONE PermissionSet slot of the existing BaseGroup, preserving its object
//        identity. Measured blast radius: GetGroupObjectMetadataSummariesOfType has exactly 5
//        callers in Ncl (NCLMetadata, NCLMetaQuery, NavAppGroup's own lazy, and two
//        DatabaseIndexDataProvider iterators), and only calls passing ObjectType.PermissionSet
//        can behave differently, because every slot is empty today.
//
// THE ORDERING TRAP, AND WHY A FRESH LazyEx IS INSTALLED EVERY TIME
//   permissionSetLookup is a LazyEx: whatever reads PermissionSetGroupObjectMetadataSummaries
//   FIRST freezes the answer. Measured: it is not yet forced at session setup, so filling the
//   slot early would usually work — "usually" being the problem. A population that lands after
//   the first read caches empty forever and looks exactly like a fix that silently does
//   nothing. Installing a fresh LazyEx over the rebuilt slot makes this order-independent
//   instead of order-lucky, and makes re-population after the inventory grows correct too.
//
// WHAT THE SYNTHESISED OWNER CARRIES
//   Each summary needs a NavAppRuntimeMetadata as its FullObjectOwner, and
//   PermissionDataProviderBase dereferences FullObjectOwner.AppId. Measured against
//   Microsoft.Dynamics.Nav.Apps.dll: the type has two public constructors, no factory, and
//   every parameter is a manifest-level value or a nullable reference — nothing is a service
//   or a publication record. AppId, Name are filled from what the runner actually knows (the
//   AL source declaration, or the dependency .app's SymbolReference.json root). Publisher and
//   Version are left null/default DELIBERATELY: the runner's symbol cache does not extract
//   them, adding that would force a CacheVersion bump that invalidates every cached symbol
//   file, and no consumer on this path reads them. That is the audit note
//   .claude/rules/precompiled-dll-respect.md asks for — not an oversight.
//
// WHAT THIS DOES NOT DO
//   The NCLMetaPermissionSet objects registered here carry NO permission rows. Their
//   Permissions list needs each set's `Permissions` array from SymbolReference.json, which the
//   symbol cache does not extract yet. So this makes the metadata layer RESOLVE — the two
//   things #2893 names — and it does not by itself put rows in Permission (2000000005),
//   Metadata Permission (2000000251) or Expanded Permission (2000000254). That is #2886, and
//   it needs the permission masks plus the virtual-table dispatch for those three tables.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private const int PermissionSetObjectTypeOrdinal = 20;   // ObjectType.PermissionSet, asserted below

    private static readonly object _permMetaGate = new();
    private static int _permMetaPopulatedForCount = -1;

    // Reflection surface. Every one of these is load-bearing: a missing member means the Ncl
    // shape moved and this file would silently populate nothing, so each throws by name.
    private static bool _permMetaReflectionReady;
    private static Type? _tNavAppGroupPM;
    private static FieldInfo? _fBaseGroupPM;
    private static FieldInfo? _fSummariesByType;
    private static FieldInfo? _fPermissionSetLookup;
    private static Type? _tSummary;
    private static ConstructorInfo? _ctorSummary;
    private static Type? _tFrozenSharingArrayOpen;
    private static Type? _tGroupSummaryComparer;
    private static FieldInfo? _fComparerInstance;
    private static Type? _tNavCode;
    private static ConstructorInfo? _ctorNavCode;
    private static Type? _tObjectTypePM;
    private static Type? _tAppRuntimeMetadata;
    private static ConstructorInfo? _ctorAppRuntimeMetadata;
    private static Type? _tAppId;
    private static ConstructorInfo? _ctorAppId;

    private static readonly Dictionary<Guid, object> _appOwnerCache = new();

    /// <summary>
    /// Make BC's own permission metadata layer answer for every permission set the runner
    /// knows: one <c>NavAppGroupObjectMetadataSummary</c> per set in the app group's
    /// PermissionSet slot, and one resolvable <c>NCLMetaPermissionSet</c> per set in the
    /// runner's NCLMetadata cache.
    ///
    /// Idempotent and re-runnable: it recomputes when the known-permission-set count has
    /// changed since the last run, and always installs a fresh lazy, so calling it again after
    /// the inventory grows is correct rather than a no-op (see the ordering note in the file
    /// banner).
    /// </summary>
    internal static void EnsurePermissionMetadataPopulated()
    {
        lock (_permMetaGate)
        {
            var known = EnumerateKnownPermissionSets()
                .GroupBy(p => p.PermissionSet.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (known.Count == _permMetaPopulatedForCount) return;

            EnsurePermissionMetadataReflection();

            var baseGroup = _fBaseGroupPM!.GetValue(null);
            if (baseGroup == null)
                throw new InvalidOperationException(
                    "NavAppGroup.BaseGroup is null — the permission metadata layer cannot be populated; "
                    + "Ncl shape changed or the skeleton session was not set up");

            var summaries = new List<object>(known.Count);
            foreach (var (permissionSet, owningAppId, owningAppName) in known)
            {
                var owner = GetOrCreateAppOwner(owningAppId, owningAppName);
                summaries.Add(_ctorSummary!.Invoke(new object?[]
                {
                    baseGroup, owner,
                    Enum.ToObject(_tObjectTypePM!, PermissionSetObjectTypeOrdinal),
                    permissionSet.Id, permissionSet.Name, string.Empty,
                }));
            }

            InstallPermissionSetSlot(baseGroup, summaries);
            InstallFreshPermissionSetLookup(baseGroup, summaries);

            _permMetaPopulatedForCount = known.Count;
            _permissionSetIdByName = null;   // the inventory just changed; rebuild lazily

            if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_PERMMETA") == "1")
            {
                var lookup = _tNavAppGroupPM!
                    .GetProperty("PermissionSetGroupObjectMetadataSummaries",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(baseGroup) as IEnumerable;
                var lookupCount = 0;
                if (lookup != null) foreach (var _ in lookup) lookupCount++;
                var resolvable = known.Count(p => EnsurePermissionSetInMetadataCache(p.PermissionSet.Id) != null);
                var declaredPermissions = known.Sum(p => p.PermissionSet.Permissions?.Count ?? 0);
                Console.Error.WriteLine(
                    $"[perm-metadata] app-group permission-set summaries: {lookupCount}; "
                    + $"meta permission sets resolvable: {resolvable}/{known.Count}; "
                    + $"declared permissions: {declaredPermissions}");
            }
        }
    }

    /// <summary>
    /// Rebuild the app group's <c>ObjectType.PermissionSet</c> slot. Sorted with BC's own
    /// <c>GroupSummaryComparer</c> and wrapped in the same <c>FrozenSharingArray</c> the
    /// constructor would have produced, so every reader — including the two
    /// DatabaseIndexDataProvider iterators — sees the shape BC expects rather than a
    /// look-alike collection.
    /// </summary>
    private static void InstallPermissionSetSlot(object baseGroup, List<object> summaries)
    {
        var arr = _fSummariesByType!.GetValue(baseGroup) as Array
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppGroup.groupObjectMetadataSummariesByType",
                "holds a value that is not an array, so the PermissionSet slot cannot be indexed — BC's permission-set metadata inventory cannot be populated");
        if (PermissionSetObjectTypeOrdinal >= arr.Length)
            throw PermissionMetadataBcShapeGap(
                "NavAppGroup.groupObjectMetadataSummariesByType",
                $"has {arr.Length} slots, so ObjectType.PermissionSet ({PermissionSetObjectTypeOrdinal}) is out of range — BC's permission-set metadata inventory cannot be populated");

        var comparer = _fComparerInstance!.GetValue(null)
            ?? Activator.CreateInstance(_tGroupSummaryComparer!)!;

        // GroupSummaryComparer implements IComparer<NavAppGroupObjectMetadataSummary> and NOT
        // the non-generic IComparer, so Array.Sort's non-generic overload cannot take it —
        // measured, it throws InvalidCastException. Drive its Compare(x, y) directly instead,
        // which is the same ordering BC's own constructor applies before freezing the array.
        var compare = _tGroupSummaryComparer!.GetMethod("Compare",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppGroup.GroupSummaryComparer.Compare",
                "method not found — BC's permission-set metadata inventory cannot be populated");
        summaries.Sort((x, y) => (int)compare.Invoke(comparer, new[] { x, y })!);

        var typed = Array.CreateInstance(_tSummary!, summaries.Count);
        for (var i = 0; i < summaries.Count; i++) typed.SetValue(summaries[i], i);

        var frozenType = _tFrozenSharingArrayOpen!.MakeGenericType(_tSummary!);
        var ctor = frozenType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 2)
            ?? throw PermissionMetadataBcShapeGap(
                "FrozenSharingArray<T>(IReadOnlyList<T>, IComparer<T>)",
                "constructor not found — BC's permission-set metadata inventory cannot be populated");
        arr.SetValue(ctor.Invoke(new[] { typed, comparer }), PermissionSetObjectTypeOrdinal);
    }

    /// <summary>
    /// Replace <c>permissionSetLookup</c> with a fresh <c>LazyEx</c> over a dictionary built
    /// exactly as BC's own constructor delegate builds it — <c>new NavCode(30, ObjectName)</c>
    /// per summary, first declaration of a name winning. Replacing rather than relying on the
    /// existing lazy still being unforced is the whole point; see the file banner.
    /// </summary>
    private static void InstallFreshPermissionSetLookup(object baseGroup, List<object> summaries)
    {
        var lazyField = _fPermissionSetLookup!;
        var lazyType = lazyField.FieldType;                       // LazyEx<Dictionary<NavCode, Summary>>
        var dictType = lazyType.GetGenericArguments()[0];
        var dict = Activator.CreateInstance(dictType)!;
        var add = dictType.GetMethod("Add")!;
        var nameProp = _tSummary!.GetProperty("ObjectName",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

        foreach (var s in summaries)
        {
            var key = _ctorNavCode!.Invoke(new object?[] { 30, nameProp.GetValue(s) });
            // BC swallows the duplicate-key ArgumentException in its own delegate; do the same
            // rather than letting one repeated role id abort the whole population.
            try { add.Invoke(dict, new[] { key, s }); }
            catch (TargetInvocationException tie) when (tie.InnerException is ArgumentException) { }
        }

        // A Func<TDict> returning the dictionary, built as an expression tree so the delegate
        // type matches the closed generic BC declares. Constructing the delegate by hand is
        // what makes this work without a compile-time reference to LazyEx<T>.
        var funcType = typeof(Func<>).MakeGenericType(dictType);
        var factory = Expression.Lambda(funcType, Expression.Constant(dict, dictType)).Compile();
        var lazyCtor = lazyType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1
                                 && c.GetParameters()[0].ParameterType == funcType)
            ?? throw PermissionMetadataBcShapeGap(
                "LazyEx<T>(Func<T>)",
                "constructor not found — BC's permission-set metadata inventory cannot be populated");
        FieldPoke.SetInstance(lazyField, baseGroup, lazyCtor.Invoke(new object?[] { factory }));
    }

    /// <summary>
    /// One <c>NavAppRuntimeMetadata</c> per owning app, carrying what the runner genuinely
    /// knows. See the file banner for why Publisher/Version are left unset rather than
    /// invented.
    /// </summary>
    private static object GetOrCreateAppOwner(Guid appId, string appName)
    {
        if (_appOwnerCache.TryGetValue(appId, out var cached)) return cached;

        var ps = _ctorAppRuntimeMetadata!.GetParameters();
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (p.ParameterType == _tAppId) { args[i] = _ctorAppId!.Invoke(new object?[] { appId }); continue; }
            args[i] = p.Name switch
            {
                "name" => appName,
                _ when p.ParameterType == typeof(string) => string.Empty,
                _ when p.ParameterType == typeof(Guid) => Guid.Empty,
                _ when p.ParameterType == typeof(bool) => false,
                _ when p.ParameterType == typeof(int) => 0,
                _ when p.ParameterType.IsEnum => Enum.GetValues(p.ParameterType).GetValue(0),
                _ when p.ParameterType.IsValueType => Activator.CreateInstance(p.ParameterType),
                // AppTenantId, Sha256Hash and the three CodeAnalysis manifests have private
                // constructors and no runner-side source. Measured: the constructor accepts
                // null for all five and the object answers AppId/Name afterwards.
                _ => null,
            };
        }

        var owner = _ctorAppRuntimeMetadata.Invoke(args);
        _appOwnerCache[appId] = owner;
        return owner;
    }

    private static void EnsurePermissionMetadataReflection()
    {
        if (_permMetaReflectionReady) return;

        var ncl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl")
            ?? throw new InvalidOperationException("Microsoft.Dynamics.Nav.Ncl is not loaded");
        var types = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("Microsoft.Dynamics.Nav.Types is not loaded");

        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        _tNavAppGroupPM = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroup")
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppGroup",
                "type not found in Ncl — BC's permission-set metadata inventory cannot be populated");
        _fBaseGroupPM = _tNavAppGroupPM.GetField("BaseGroup", BindingFlags.Public | BindingFlags.Static)
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppGroup.BaseGroup",
                "static field not found — BC's permission-set metadata inventory cannot be populated");
        _fSummariesByType = _tNavAppGroupPM.GetField("groupObjectMetadataSummariesByType", Inst)
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppGroup.groupObjectMetadataSummariesByType",
                "field not found — BC's permission-set metadata inventory cannot be populated");
        _fPermissionSetLookup = _tNavAppGroupPM.GetField("permissionSetLookup", Inst)
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppGroup.permissionSetLookup",
                "field not found — BC's permission-set metadata inventory cannot be populated");

        _tSummary = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroupObjectMetadataSummary")
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppGroupObjectMetadataSummary",
                "type not found in Ncl — BC's permission-set metadata inventory cannot be populated");
        _ctorSummary = _tSummary.GetConstructors(Inst).FirstOrDefault(c => c.GetParameters().Length == 6)
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppGroupObjectMetadataSummary(NavAppGroup, NavAppRuntimeMetadata, ObjectType, int, string, string)",
                "constructor not found — BC's permission-set metadata inventory cannot be populated");

        _tFrozenSharingArrayOpen = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.FrozenSharingArray`1")
            ?? throw PermissionMetadataBcShapeGap(
                "FrozenSharingArray`1",
                "type not found in Ncl — BC's permission-set metadata inventory cannot be populated");
        _tGroupSummaryComparer = _tNavAppGroupPM.GetNestedType("GroupSummaryComparer",
            BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppGroup.GroupSummaryComparer",
                "nested type not found — BC's permission-set metadata inventory cannot be populated");
        _fComparerInstance = _tGroupSummaryComparer.GetField("Instance",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppGroup.GroupSummaryComparer.Instance",
                "static field not found — BC's permission-set metadata inventory cannot be populated");

        _tNavCode = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NavCode")
            ?? types.GetType("Microsoft.Dynamics.Nav.Types.NavCode")
            ?? throw PermissionMetadataBcShapeGap(
                "NavCode",
                "type not found in Ncl or Types — BC's permission-set metadata inventory cannot be populated");
        _ctorNavCode = _tNavCode.GetConstructors(Inst).FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == 2 && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(string);
        }) ?? throw PermissionMetadataBcShapeGap(
            "NavCode(int, string)",
            "constructor not found — BC's permission-set metadata inventory cannot be populated");

        _tObjectTypePM = types.GetType("Microsoft.Dynamics.Nav.Types.ObjectType")
            ?? throw PermissionMetadataBcShapeGap(
                "ObjectType",
                "type not found in Types — BC's permission-set metadata inventory cannot be populated");
        if (!Enum.IsDefined(_tObjectTypePM, PermissionSetObjectTypeOrdinal)
            || Enum.ToObject(_tObjectTypePM, PermissionSetObjectTypeOrdinal).ToString() != "PermissionSet")
            throw PermissionMetadataBcShapeGap(
                "ObjectType",
                $"value {PermissionSetObjectTypeOrdinal} is not PermissionSet in this BC build, so the slot index this file writes would be the wrong object type — BC's permission-set metadata inventory cannot be populated");

        var apps = AppDomain.CurrentDomain.GetAssemblies()
                       .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Apps")
                   ?? Assembly.Load("Microsoft.Dynamics.Nav.Apps");
        _tAppRuntimeMetadata = apps.GetType("Microsoft.Dynamics.Nav.Apps.Runtime.NavAppRuntimeMetadata")
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppRuntimeMetadata",
                "type not found in Microsoft.Dynamics.Nav.Apps — BC's permission-set metadata inventory cannot be populated");
        // The SHORTER of the two public constructors: same values minus the summaries and
        // dependencies lists, which a synthesised owner has nothing truthful to put in.
        _ctorAppRuntimeMetadata = _tAppRuntimeMetadata.GetConstructors()
            .OrderBy(c => c.GetParameters().Length).FirstOrDefault()
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppRuntimeMetadata",
                "has no public constructor — BC's permission-set metadata inventory cannot be populated");
        _tAppId = _ctorAppRuntimeMetadata.GetParameters()
            .FirstOrDefault(p => p.ParameterType.Name == "AppId")?.ParameterType
            ?? throw PermissionMetadataBcShapeGap(
                "NavAppRuntimeMetadata..ctor",
                "takes no AppId parameter — BC's permission-set metadata inventory cannot be populated");
        _ctorAppId = _tAppId.GetConstructors().FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == 1 && p[0].ParameterType == typeof(Guid);
        }) ?? throw PermissionMetadataBcShapeGap(
            "AppId(Guid)",
            "constructor not found — BC's permission-set metadata inventory cannot be populated");

        _permMetaReflectionReady = true;
    }

    private static readonly Dictionary<int, object?> _metaPermissionSetCache = new();
    private static MethodInfo? _mCreateEmptyMetaPermissionSet;

    /// <summary>
    /// The <c>NCLMetaPermissionSet</c> for one permission set id, built from the runner's own
    /// inventory and cached. Null when the runner has no declaration for that id — the caller
    /// then falls through to BC's own <c>NavMetadataNotFoundException</c>, which is what
    /// "there is no such permission set" has to look like.
    ///
    /// <para><c>metadataLoaded</c> is poked true for the same reason
    /// <see cref="RecordPatches.BuildNCLMetaTable"/> does it: BC loads an application object's
    /// metadata LAZILY, on first property access, not at construction —
    /// <c>NCLMetaPermissionSet.LoadMetadata()</c> calls <c>LoadPermissionSetMetadata()</c>,
    /// which goes to the <c>INCLMetaApplicationObjectLoader</c> this instance has none of.
    /// Marking it loaded is the shape already solved for NCLMetaTable and NCLMetaQuery, not a
    /// second mechanism.</para>
    /// </summary>
    internal static object? EnsurePermissionSetInMetadataCache(int permissionSetId)
    {
        lock (_permMetaGate)
        {
            if (_metaPermissionSetCache.TryGetValue(permissionSetId, out var cached)) return cached;
            var built = BuildNclMetaPermissionSet(permissionSetId);
            _metaPermissionSetCache[permissionSetId] = built;
            return built;
        }
    }

    private static object? BuildNclMetaPermissionSet(int permissionSetId)
    {
        var declaration = EnumerateKnownPermissionSets()
            .Select(p => p.PermissionSet)
            .FirstOrDefault(p => p.Id == permissionSetId);
        if (declaration == null) return null;

        EnsurePermissionMetadataReflection();

        var ncl = _tNavAppGroupPM!.Assembly;
        var metaType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaPermissionSet")
            ?? throw PermissionMetadataBcShapeGap(
                "NCLMetaPermissionSet",
                "type not found in Ncl — BC's permission-set metadata inventory cannot be populated");
        _mCreateEmptyMetaPermissionSet ??= metaType.GetMethod("CreateEmptyNCLMetaPermissionSet",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw PermissionMetadataBcShapeGap(
                "NCLMetaPermissionSet.CreateEmptyNCLMetaPermissionSet",
                "static method not found — BC's permission-set metadata inventory cannot be populated");

        var baseGroup = _fBaseGroupPM!.GetValue(null);
        var meta = _mCreateEmptyMetaPermissionSet.Invoke(null,
            new object?[] { null, permissionSetId, baseGroup, -1, string.Empty })!;

        // #2910: hand BC a real MetaPermissionSet and let its OWN AssignFromMetaPermissionSet
        // fill the instance — Name, Access, Assignable, the caption strings, the includes and
        // excludes, and Permissions as PermissionDefinition objects. Poking the backing fields
        // by hand (what this did before) could only ever fill the ones somebody remembered;
        // this way every field BC sets is set, by BC.
        var assign = metaType.GetMethod("AssignFromMetaPermissionSet",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw PermissionMetadataBcShapeGap(
                "NCLMetaPermissionSet.AssignFromMetaPermissionSet",
                "method not found — BC's permission-set metadata inventory cannot be populated");
        assign.Invoke(meta, new[] { BuildMetaPermissionSet(declaration) });

        EnsureCachePopulatorReflection();
        if (_fNCLMetaAppObjMetadataLoaded != null)
            FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);

        return meta;
    }

    private static void SetBackingField(Type declaring, object target, string propertyName, object? value)
    {
        var f = declaring.GetField($"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw PermissionMetadataBcShapeGap(
                $"NCLMetaPermissionSet.{propertyName}",
                "has no backing field — BC's permission-set metadata inventory cannot be populated");
        FieldPoke.SetInstance(f, target, value);
    }

    private static void SetEmptyListBackingField(Type declaring, object target, string propertyName)
    {
        var prop = declaring.GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw PermissionMetadataBcShapeGap(
                $"NCLMetaPermissionSet.{propertyName}",
                "property not found — BC's permission-set metadata inventory cannot be populated");
        var element = prop.PropertyType.IsGenericType
            ? prop.PropertyType.GetGenericArguments()[0]
            : typeof(object);
        SetBackingField(declaring, target, propertyName,
            Activator.CreateInstance(typeof(List<>).MakeGenericType(element)));
    }

    // ── source-declared permissions: names in, ids out (#2910) ──────────────────────────

    /// <summary>
    /// Turn a source-declared set's permission entries into the id-based shape a precompiled
    /// .app already states, by resolving each object NAME against this run's own parsed object
    /// declarations. AL has no id form in a permission set, so this resolution is the only way
    /// a permission set the runner compiled can mean the same thing as one that arrived
    /// precompiled.
    ///
    /// <para>KNOWN LIMIT, deliberately not hidden: <c>ParsedObjectDecls</c> does not carry
    /// tables (they are parsed by the table pipeline, not the declaration sweep), so a
    /// <c>tabledata</c> or <c>table</c> grant in a set the runner compiled from source is
    /// dropped rather than guessed at. Every tabledata grant that matters in practice comes
    /// from a precompiled dependency, where SymbolReference.json states the id outright and
    /// this method is not involved. Dropping is the conservative answer: inventing an id would
    /// put a permission row on the wrong object, which nothing downstream could detect.</para>
    /// </summary>
    private static IReadOnlyList<BcAppSymbolCache.PermissionSymbol> ResolveSourcePermissionEntries(
        IReadOnlyList<ParsedAlPermissionEntry>? entries)
    {
        if (entries == null || entries.Count == 0) return Array.Empty<BcAppSymbolCache.PermissionSymbol>();

        var byName = new Dictionary<(string Kind, string Name), int>(new KindNameComparer());
        foreach (var decl in ParsedObjectDecls)
            byName[(decl.Kind, decl.Name)] = decl.Id;

        var resolved = new List<BcAppSymbolCache.PermissionSymbol>();
        foreach (var e in entries)
        {
            var kind = AlKeywordForPermissionObject(e.ObjectTypeOrdinal);
            if (kind == null) continue;
            if (!byName.TryGetValue((kind, e.ObjectName), out var id))
            {
                if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_PERMMETA") == "1")
                    Console.Error.WriteLine(
                        $"[perm-metadata] source permission on {kind} '{e.ObjectName}' not resolvable "
                        + "to an object id in this run — dropped rather than guessed");
                continue;
            }
            resolved.Add(new BcAppSymbolCache.PermissionSymbol(e.ObjectTypeOrdinal, id, e.Mask));
        }
        return resolved;
    }

    private sealed class KindNameComparer : IEqualityComparer<(string Kind, string Name)>
    {
        public bool Equals((string Kind, string Name) x, (string Kind, string Name) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Kind, y.Kind)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((string Kind, string Name) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Kind),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }

    /// <summary>
    /// The AL object keyword a <c>PermissionObject</c> ordinal names, for the kinds
    /// <c>ParsedObjectDecls</c> can answer for. Tables are absent on purpose — see
    /// <see cref="ResolveSourcePermissionEntries"/>.
    /// </summary>
    private static string? AlKeywordForPermissionObject(int ordinal) => ordinal switch
    {
        3 => "report",
        5 => "codeunit",
        6 => "xmlport",
        8 => "page",
        9 => "query",
        _ => null,
    };

    private static Type? _tMetaPermissionSet;
    private static Type? _tMetaPermission;
    private static Type? _tAccessModifier;

    /// <summary>
    /// The <c>MetaPermissionSet</c> BC's own <c>AssignFromMetaPermissionSet</c> consumes, built
    /// from the runner's inventory. Public parameterless constructor, every property settable —
    /// this is transcription of data the run already has, not a re-implementation of anything:
    /// the composition (includes expansion, exclusions, extension merge) stays in BC's
    /// PermissionSetGraphWalker and PermissionComposer, which is the whole point of feeding
    /// them real data instead of computing rows here.
    /// </summary>
    private static object BuildMetaPermissionSet(BcAppSymbolCache.PermissionSetSymbol declaration)
    {
        var types = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("Microsoft.Dynamics.Nav.Types is not loaded");

        _tMetaPermissionSet ??= types.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaPermissionSet")
            ?? throw PermissionMetadataBcShapeGap(
                "MetaPermissionSet",
                "type not found in Types — BC's permission-set metadata inventory cannot be populated");
        _tMetaPermission ??= types.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaPermission")
            ?? throw PermissionMetadataBcShapeGap(
                "MetaPermission",
                "type not found in Types — BC's permission-set metadata inventory cannot be populated");
        // AccessModifier is the enum MetaPermissionSet.Access is declared as. Take it FROM
        // that property rather than from a namespace guess: it does not live where the
        // sibling metadata types do, and hardcoding a namespace turned into a run-aborting
        // "Types shape changed" on the first real run.
        _tAccessModifier ??= _tMetaPermissionSet.GetProperty("Access",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType
            ?? throw PermissionMetadataBcShapeGap(
                "MetaPermissionSet.Access",
                "property not found — BC's permission-set metadata inventory cannot be populated");

        var mps = Activator.CreateInstance(_tMetaPermissionSet)!;
        SetProperty(mps, "Id", declaration.Id);
        SetProperty(mps, "Name", declaration.Name);
        SetProperty(mps, "Assignable", declaration.Assignable);
        // AL's default when a set declares no Access is Public, the same default the AL
        // compiler applies; an unrecognised spelling is treated as the default rather than
        // failing the run, because Access does not affect what a permission grants.
        SetProperty(mps, "Access",
            declaration.Access != null && Enum.TryParse(_tAccessModifier, declaration.Access, ignoreCase: true, out var access)
                ? access!
                : Enum.ToObject(_tAccessModifier, 0));

        var permListType = typeof(List<>).MakeGenericType(_tMetaPermission);
        var permissions = (System.Collections.IList)Activator.CreateInstance(permListType)!;
        foreach (var p in declaration.Permissions ?? (IReadOnlyList<BcAppSymbolCache.PermissionSymbol>)Array.Empty<BcAppSymbolCache.PermissionSymbol>())
        {
            // MetaPermission is a STRUCT: box it once, set the three properties on the box,
            // then add — adding first would copy an empty value into the list.
            var mp = Activator.CreateInstance(_tMetaPermission)!;
            SetProperty(mp, "Id", p.ObjectId);
            SetProperty(mp, "Value", p.Value);
            var objectTypeProp = _tMetaPermission.GetProperty("Type",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw PermissionMetadataBcShapeGap(
                    "MetaPermission.Type",
                    "property not found — BC's permission-set metadata inventory cannot be populated");
            objectTypeProp.SetValue(mp, Enum.ToObject(objectTypeProp.PropertyType, p.ObjectType));
            permissions.Add(mp);
        }
        SetProperty(mps, "Permissions", permissions);

        SetProperty(mps, "IncludedPermissionSets",
            BuildIncludeList(_tMetaPermissionSet.GetProperty("IncludedPermissionSets")!.PropertyType,
                declaration.IncludedPermissionSets));
        SetProperty(mps, "ExcludedPermissionSets",
            BuildIncludeList(_tMetaPermissionSet.GetProperty("ExcludedPermissionSets")!.PropertyType, null));

        return mps;
    }

    /// <summary>
    /// The include/exclude list in whatever element type BC declares. Measured on BC 28.1 it is
    /// <c>List&lt;int&gt;</c> — permission set OBJECT IDS, not names — while both SymbolReference.json
    /// and AL source state NAMES, so the names are resolved against this run's own inventory
    /// here. A name that resolves to nothing is dropped with a diagnostic rather than guessed
    /// at: an invented id would make BC compose a different permission set's grants into this
    /// one, which nothing downstream could detect.
    ///
    /// <para>The element type is read from the property rather than assumed, because the first
    /// version of this code assumed strings and failed loudly at <c>Add()</c> — the right
    /// failure, but only because it was checked at all.</para>
    /// </summary>
    private static object BuildIncludeList(Type listType, IReadOnlyList<string>? names)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
        if (names == null || names.Count == 0) return list;

        var element = listType.IsGenericType ? listType.GetGenericArguments()[0] : typeof(string);
        foreach (var name in names)
        {
            if (element == typeof(string)) { list.Add(name); continue; }
            if (element == typeof(int))
            {
                if (PermissionSetIdByName().TryGetValue(name, out var id)) { list.Add(id); continue; }
                if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_PERMMETA") == "1")
                    Console.Error.WriteLine(
                        $"[perm-metadata] included permission set '{name}' is not in this run's inventory — dropped");
                continue;
            }
            var ctor = element.GetConstructors()
                .FirstOrDefault(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length == 2 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(string);
                });
            if (ctor != null) { list.Add(ctor.Invoke(new object?[] { 30, name })); continue; }
            throw PermissionMetadataBcShapeGap(
                $"MetaPermissionSet include/exclude list element type {element.Name}",
                "is not one this code knows how to fill — BC's permission-set metadata inventory cannot be populated");
        }
        return list;
    }

    private static Dictionary<string, int>? _permissionSetIdByName;

    /// <summary>Role id -> object id over every permission set the runner knows, built once per population.</summary>
    private static Dictionary<string, int> PermissionSetIdByName()
    {
        if (_permissionSetIdByName != null) return _permissionSetIdByName;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (permissionSet, _, _) in EnumerateKnownPermissionSets())
            index.TryAdd(permissionSet.Name, permissionSet.Id);
        return _permissionSetIdByName = index;
    }

    private static void SetProperty(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw PermissionMetadataBcShapeGap(
                $"{target.GetType().Name}.{name}",
                "property not found — BC's permission-set metadata inventory cannot be populated");
        prop.SetValue(target, value);
    }
}

