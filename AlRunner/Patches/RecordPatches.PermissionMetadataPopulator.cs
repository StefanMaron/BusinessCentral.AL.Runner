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

            if (Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_PERMMETA") == "1")
            {
                var lookup = _tNavAppGroupPM!
                    .GetProperty("PermissionSetGroupObjectMetadataSummaries",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(baseGroup) as IEnumerable;
                var lookupCount = 0;
                if (lookup != null) foreach (var _ in lookup) lookupCount++;
                var resolvable = known.Count(p => EnsurePermissionSetInMetadataCache(p.PermissionSet.Id) != null);
                Console.Error.WriteLine(
                    $"[perm-metadata] app-group permission-set summaries: {lookupCount}; "
                    + $"meta permission sets resolvable: {resolvable}/{known.Count}");
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
            ?? throw new InvalidOperationException(
                "NavAppGroup.groupObjectMetadataSummariesByType is not an array — Ncl shape changed; do not commit");
        if (PermissionSetObjectTypeOrdinal >= arr.Length)
            throw new InvalidOperationException(
                $"NavAppGroup.groupObjectMetadataSummariesByType has {arr.Length} slots, "
                + $"so ObjectType.PermissionSet ({PermissionSetObjectTypeOrdinal}) is out of range — Ncl shape changed; do not commit");

        var comparer = _fComparerInstance!.GetValue(null)
            ?? Activator.CreateInstance(_tGroupSummaryComparer!)!;

        // GroupSummaryComparer implements IComparer<NavAppGroupObjectMetadataSummary> and NOT
        // the non-generic IComparer, so Array.Sort's non-generic overload cannot take it —
        // measured, it throws InvalidCastException. Drive its Compare(x, y) directly instead,
        // which is the same ordering BC's own constructor applies before freezing the array.
        var compare = _tGroupSummaryComparer!.GetMethod("Compare",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "GroupSummaryComparer.Compare not found — Ncl shape changed; do not commit");
        summaries.Sort((x, y) => (int)compare.Invoke(comparer, new[] { x, y })!);

        var typed = Array.CreateInstance(_tSummary!, summaries.Count);
        for (var i = 0; i < summaries.Count; i++) typed.SetValue(summaries[i], i);

        var frozenType = _tFrozenSharingArrayOpen!.MakeGenericType(_tSummary!);
        var ctor = frozenType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 2)
            ?? throw new InvalidOperationException(
                "FrozenSharingArray<T>(IReadOnlyList<T>, IComparer<T>) not found — Ncl shape changed; do not commit");
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
            ?? throw new InvalidOperationException(
                "LazyEx<T>(Func<T>) not found — Ncl shape changed; do not commit");
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
            ?? throw new InvalidOperationException("NavAppGroup not found — Ncl shape changed; do not commit");
        _fBaseGroupPM = _tNavAppGroupPM.GetField("BaseGroup", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("NavAppGroup.BaseGroup not found — Ncl shape changed; do not commit");
        _fSummariesByType = _tNavAppGroupPM.GetField("groupObjectMetadataSummariesByType", Inst)
            ?? throw new InvalidOperationException(
                "NavAppGroup.groupObjectMetadataSummariesByType not found — Ncl shape changed; do not commit");
        _fPermissionSetLookup = _tNavAppGroupPM.GetField("permissionSetLookup", Inst)
            ?? throw new InvalidOperationException(
                "NavAppGroup.permissionSetLookup not found — Ncl shape changed; do not commit");

        _tSummary = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroupObjectMetadataSummary")
            ?? throw new InvalidOperationException(
                "NavAppGroupObjectMetadataSummary not found — Ncl shape changed; do not commit");
        _ctorSummary = _tSummary.GetConstructors(Inst).FirstOrDefault(c => c.GetParameters().Length == 6)
            ?? throw new InvalidOperationException(
                "NavAppGroupObjectMetadataSummary(NavAppGroup, NavAppRuntimeMetadata, ObjectType, int, string, string) "
                + "not found — Ncl shape changed; do not commit");

        _tFrozenSharingArrayOpen = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.FrozenSharingArray`1")
            ?? throw new InvalidOperationException("FrozenSharingArray`1 not found — Ncl shape changed; do not commit");
        _tGroupSummaryComparer = _tNavAppGroupPM.GetNestedType("GroupSummaryComparer",
            BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "NavAppGroup.GroupSummaryComparer not found — Ncl shape changed; do not commit");
        _fComparerInstance = _tGroupSummaryComparer.GetField("Instance",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "GroupSummaryComparer.Instance not found — Ncl shape changed; do not commit");

        _tNavCode = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NavCode")
            ?? types.GetType("Microsoft.Dynamics.Nav.Types.NavCode")
            ?? throw new InvalidOperationException("NavCode not found — Ncl shape changed; do not commit");
        _ctorNavCode = _tNavCode.GetConstructors(Inst).FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == 2 && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(string);
        }) ?? throw new InvalidOperationException("NavCode(int, string) not found — Ncl shape changed; do not commit");

        _tObjectTypePM = types.GetType("Microsoft.Dynamics.Nav.Types.ObjectType")
            ?? throw new InvalidOperationException("ObjectType not found — Types shape changed; do not commit");
        if (!Enum.IsDefined(_tObjectTypePM, PermissionSetObjectTypeOrdinal)
            || Enum.ToObject(_tObjectTypePM, PermissionSetObjectTypeOrdinal).ToString() != "PermissionSet")
            throw new InvalidOperationException(
                $"ObjectType value {PermissionSetObjectTypeOrdinal} is not PermissionSet in this BC build "
                + "— the slot index this file writes would be the wrong object type; do not commit");

        var apps = AppDomain.CurrentDomain.GetAssemblies()
                       .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Apps")
                   ?? Assembly.Load("Microsoft.Dynamics.Nav.Apps");
        _tAppRuntimeMetadata = apps.GetType("Microsoft.Dynamics.Nav.Apps.Runtime.NavAppRuntimeMetadata")
            ?? throw new InvalidOperationException(
                "NavAppRuntimeMetadata not found — Microsoft.Dynamics.Nav.Apps shape changed; do not commit");
        // The SHORTER of the two public constructors: same values minus the summaries and
        // dependencies lists, which a synthesised owner has nothing truthful to put in.
        _ctorAppRuntimeMetadata = _tAppRuntimeMetadata.GetConstructors()
            .OrderBy(c => c.GetParameters().Length).FirstOrDefault()
            ?? throw new InvalidOperationException(
                "NavAppRuntimeMetadata has no public constructor — Apps shape changed; do not commit");
        _tAppId = _ctorAppRuntimeMetadata.GetParameters()
            .FirstOrDefault(p => p.ParameterType.Name == "AppId")?.ParameterType
            ?? throw new InvalidOperationException(
                "NavAppRuntimeMetadata's constructor takes no AppId — Apps shape changed; do not commit");
        _ctorAppId = _tAppId.GetConstructors().FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == 1 && p[0].ParameterType == typeof(Guid);
        }) ?? throw new InvalidOperationException("AppId(Guid) not found — Common shape changed; do not commit");

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
            ?? throw new InvalidOperationException(
                "NCLMetaPermissionSet not found — Ncl shape changed; do not commit");
        _mCreateEmptyMetaPermissionSet ??= metaType.GetMethod("CreateEmptyNCLMetaPermissionSet",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "NCLMetaPermissionSet.CreateEmptyNCLMetaPermissionSet not found — Ncl shape changed; do not commit");

        var baseGroup = _fBaseGroupPM!.GetValue(null);
        var meta = _mCreateEmptyMetaPermissionSet.Invoke(null,
            new object?[] { null, permissionSetId, baseGroup, -1, string.Empty })!;

        SetBackingField(metaType, meta, "Name", declaration.Name);
        SetBackingField(metaType, meta, "Assignable", declaration.Assignable);
        // Empty, not null: BC reads these as lists. They stay EMPTY on purpose — the
        // permission rows themselves need each set's `Permissions` array out of
        // SymbolReference.json, which the symbol cache does not extract yet (#2886). An empty
        // list says "this set resolves and declares nothing here"; a null would NRE in the
        // first consumer that enumerated it, which is a worse answer to the same gap.
        SetEmptyListBackingField(metaType, meta, "Permissions");
        SetEmptyListBackingField(metaType, meta, "IncludedPermissionSets");
        SetEmptyListBackingField(metaType, meta, "ExcludedPermissionSets");

        EnsureCachePopulatorReflection();
        if (_fNCLMetaAppObjMetadataLoaded != null)
            FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);

        return meta;
    }

    private static void SetBackingField(Type declaring, object target, string propertyName, object? value)
    {
        var f = declaring.GetField($"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"NCLMetaPermissionSet.{propertyName} has no backing field — Ncl shape changed; do not commit");
        FieldPoke.SetInstance(f, target, value);
    }

    private static void SetEmptyListBackingField(Type declaring, object target, string propertyName)
    {
        var prop = declaring.GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"NCLMetaPermissionSet.{propertyName} not found — Ncl shape changed; do not commit");
        var element = prop.PropertyType.IsGenericType
            ? prop.PropertyType.GetGenericArguments()[0]
            : typeof(object);
        SetBackingField(declaring, target, propertyName,
            Activator.CreateInstance(typeof(List<>).MakeGenericType(element)));
    }
}

