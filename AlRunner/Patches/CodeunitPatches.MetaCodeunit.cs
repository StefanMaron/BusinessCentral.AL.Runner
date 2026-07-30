// CodeunitPatches.MetaCodeunit — Option-C polyfill for NavCodeunit.get_MetaCodeunit
//
// The real getter chains through `base.Session.NCLMetadata.GetMetaCodeunitById(...)`
// which on the skeleton runtime hits a code path that NREs (Tenants index lookup,
// or the cache-miss factory that needs an XmlMetadataLoader we don't have).
//
// All ten of the get_MetaCodeunit failures in the bucket-1/codeunit-runtime
// classification go through ALSession.ALBindSubscription → NavCodeunit.BindSubscription
// → MetaCodeunit.IsEventManualBinding. IsEventManualBinding only needs the meta's
// ApplicationObjectClrType so it can read [NavCodeunitOptionsAttribute] off the
// AL-emitted Codeunit{N} class via reflection (LoadOptionsFromAttributeOrInstance
// with onlyLoadFromAttribute=true).
//
// Strategy: hook the getter, lazy-build an NCLMetaCodeunit via the existing
// CreateEmptyNCLMetaCodeunit factory, pre-populate its private
// nclMetaObjectCLRTypeContainer with the AL-emitted Codeunit{N} type so
// ApplicationObjectClrType returns without going through CompileAndLoadClrObject,
// mark metadataLoaded=true to skip the LoadMetadata path, cache on the
// NavCodeunit instance's `metaCodeunit` field, and return.
//
// Mirrors the populate-state pattern from RecordPatches.NclMetaFormReportBuilder.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner;

public static partial class BcRuntime
{
    // Cache: codeunit ID -> built NCLMetaCodeunit (one per ID across the test run).
    private static readonly ConcurrentDictionary<int, object?> _navCodeunitMetaCache = new();

    private static FieldInfo? _fNavCodeunitMetaCodeunit;             // NavCodeunit.metaCodeunit
    private static MethodInfo? _mCreateEmptyNCLMetaCodeunit;          // NCLMetaCodeunit.CreateEmptyNCLMetaCodeunit
    private static FieldInfo? _fNCLMetaAOClrTypeContainer;            // NCLMetaApplicationObject.nclMetaObjectCLRTypeContainer
    private static FieldInfo? _fNCLMetaAOMetadataLoaded;              // NCLMetaApplicationObject.metadataLoaded
    private static Type? _tNCLMetaObjectCLRTypeContainer;             // private nested type
    private static object? _navAppGroupBaseGroup;
    private static bool _metaCodeunitReflectionFailed;

    private static void EnsureMetaCodeunitReflection()
    {
        if (_fNavCodeunitMetaCodeunit != null || _metaCodeunitReflectionFailed) return;

        var navNcl = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (navNcl == null) { _metaCodeunitReflectionFailed = true; return; }

        var tNavCodeunit  = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavCodeunit");
        var tNclMetaCu    = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaCodeunit");
        var tNclMetaAO    = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject");
        var tAppGroup     = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroup");

        _fNavCodeunitMetaCodeunit = tNavCodeunit?.GetField("metaCodeunit",
            BindingFlags.NonPublic | BindingFlags.Instance);

        _mCreateEmptyNCLMetaCodeunit = tNclMetaCu?.GetMethod("CreateEmptyNCLMetaCodeunit",
            BindingFlags.NonPublic | BindingFlags.Static);

        _fNCLMetaAOClrTypeContainer = tNclMetaAO?.GetField("nclMetaObjectCLRTypeContainer",
            BindingFlags.NonPublic | BindingFlags.Instance);

        _fNCLMetaAOMetadataLoaded = tNclMetaAO?.GetField("metadataLoaded",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Resolve the private nested container type via the field's declared type.
        _tNCLMetaObjectCLRTypeContainer = _fNCLMetaAOClrTypeContainer?.FieldType;

        _navAppGroupBaseGroup = tAppGroup?.GetProperty("BaseGroup",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? tAppGroup?.GetField("BaseGroup",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        if (_fNavCodeunitMetaCodeunit == null
            || _mCreateEmptyNCLMetaCodeunit == null
            || _fNCLMetaAOClrTypeContainer == null
            || _tNCLMetaObjectCLRTypeContainer == null)
        {
            Console.Error.WriteLine(
                "[BcRuntime] EnsureMetaCodeunitReflection: failed — "
                + $"metaCodeunit={_fNavCodeunitMetaCodeunit != null}, "
                + $"CreateEmpty={_mCreateEmptyNCLMetaCodeunit != null}, "
                + $"clrContainer={_fNCLMetaAOClrTypeContainer != null}, "
                + $"containerType={_tNCLMetaObjectCLRTypeContainer != null}");
            _metaCodeunitReflectionFailed = true;
        }
    }

    // Side-table: meta instance -> AL-emitted Codeunit{N} CLR type. Indexed by
    // RuntimeHelpers.GetHashCode (identity). Keeps a strong ref via ConditionalWeakTable
    // so meta keeps the type alive only while the meta itself lives.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Type>
        _metaToClrType = new();

    /// <summary>
    /// Replacement for NCLMetaCodeunit.get_IsEventManualBinding. Reads the
    /// NavCodeunitOptionsAttribute directly off the AL-emitted CLR type that we
    /// stashed when building the meta — bypasses LoadOptionsFromAttributeOrInstance
    /// which dereferences base.ApplicationObjectClrType (a property whose JIT body
    /// the JmpHook can't redirect on R2R-compiled call sites).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NCLMetaCodeunit_get_IsEventManualBinding(object self)
    {
        if (!_metaToClrType.TryGetValue(self, out var clrType) || clrType == null)
            return false;
        // NavCodeunitOptionsAttribute lives in Microsoft.Dynamics.Nav.Runtime; resolve
        // by name to avoid hard-binding to internal types.
        foreach (var attr in clrType.GetCustomAttributes(inherit: false))
        {
            var t = attr.GetType();
            if (t.Name != "NavCodeunitOptionsAttribute") continue;
            // Match decompile: Options & EventManualBinding != 0.  Property "Options" is
            // a NavCodeunitOptions enum; "IsEventManualBinding" is a derived bool.
            var isManual = t.GetProperty("IsEventManualBinding",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (isManual != null)
            {
                try { return (bool)isManual.GetValue(attr)!; } catch { }
            }
            var optionsProp = t.GetProperty("Options",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var v = optionsProp?.GetValue(attr);
            if (v != null)
            {
                // EventManualBinding flag = 1 in NavCodeunitOptions enum (per decompile).
                int iv = Convert.ToInt32(v);
                return (iv & 1) != 0;
            }
        }
        return false;
    }

    /// <summary>
    /// Replacement for NavCodeunit.get_MetaCodeunit. Returns a skeleton
    /// NCLMetaCodeunit pre-populated with the AL-emitted Codeunit{N} CLR type
    /// so IsEventManualBinding (and any other attribute-only readers) work
    /// without traversing the real metadata cache.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavCodeunit_get_MetaCodeunit(
        Microsoft.Dynamics.Nav.Runtime.NavCodeunit self)
    {
        EnsureMetaCodeunitReflection();
        if (_metaCodeunitReflectionFailed
            || _fNavCodeunitMetaCodeunit == null
            || _mCreateEmptyNCLMetaCodeunit == null) return null;

        // Fast-path: already built and cached on the instance.
        var existing = _fNavCodeunitMetaCodeunit.GetValue(self);
        if (existing != null) return existing;

        var clrType = self.GetType();
        int id = self.ObjectId.ObjectNumber;
        if (id == 0)
        {
            // ObjectId may not be populated on test-scope receivers; derive from class
            // name (BC-emitted convention: Codeunit{ID} for runnables, Codeunit{ID}_xxx
            // for scope/inner classes).
            var name = clrType.Name;
            if (name.StartsWith("Codeunit"))
            {
                int p = 8;
                int v = 0;
                while (p < name.Length && char.IsDigit(name[p])) { v = v * 10 + (name[p] - '0'); p++; }
                if (v > 0) id = v;
            }
        }
        // Cache by id so repeat calls across NavCodeunit instances share the meta.
        var meta = _navCodeunitMetaCache.GetOrAdd(id, _ => BuildNclMetaCodeunit(id, clrType));
        if (meta == null) return null;

        // Stash the CLR type for the IsEventManualBinding hook (which can't rely on the
        // base ApplicationObjectClrType getter — the JmpHook there isn't reached from
        // R2R-compiled call sites in NCL.dll itself).
        try { _metaToClrType.AddOrUpdate(meta, clrType); } catch { }

        // Stamp the instance field so the original getter's null-check short-circuits
        // on subsequent calls (no JmpHook overhead).
        try { FieldPoke.SetInstance(_fNavCodeunitMetaCodeunit, self, meta); }
        catch { /* best-effort */ }

        return meta;
    }

    private static object? BuildNclMetaCodeunit(int id, Type clrType)
    {
        try
        {
            // (loader, codeunitId, appGroup, depOrder=-1, alNamespace="")
            var meta = _mCreateEmptyNCLMetaCodeunit!.Invoke(null,
                new object?[] { null, id, _navAppGroupBaseGroup, -1, string.Empty });
            if (meta == null) return null;

            // Pre-populate the CLR-type container so ApplicationObjectClrType returns
            // clrType without going through CompileAndLoadClrObject.
            // Use the no-arg ctor when present (auto-property defaults are zero/null,
            // but with GetUninitializedObject + property setter we observed the
            // setter had no effect on .NET 9 — possibly trim/JIT visibility issue).
            // Construct via Activator to ensure backing fields are properly wired.
            object? container = null;
            try
            {
                container = Activator.CreateInstance(_tNCLMetaObjectCLRTypeContainer!,
                    nonPublic: true);
            }
            catch { }
            if (container == null)
                container = RuntimeHelpers.GetUninitializedObject(_tNCLMetaObjectCLRTypeContainer!);

            // Auto-property backing fields: <PropName>k__BackingField. Write directly
            // — bypasses any visibility issue with the setter on a private nested type.
            // Auto-property backing fields: <PropName>k__BackingField OR (under some
            // build configs) plain field name. Find by Name match against both.
            FieldInfo? fIsLoaded = null, fAppObjClr = null;
            foreach (var f in _tNCLMetaObjectCLRTypeContainer!.GetFields(
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.Name.Contains("IsLoaded")) fIsLoaded = f;
                else if (f.Name.Contains("ApplicationObjectClrType")) fAppObjClr = f;
            }
            if (fIsLoaded == null || fAppObjClr == null)
            {
                var allFields = string.Join(", ", _tNCLMetaObjectCLRTypeContainer
                    .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                    .Select(f => f.Name));
                Console.Error.WriteLine(
                    "[BcRuntime] BuildNclMetaCodeunit: backing-field lookup failed — "
                    + $"IsLoaded={fIsLoaded != null}, ApplicationObjectClrType={fAppObjClr != null}, "
                    + $"all fields=[{allFields}]");
                return null;
            }
            FieldPoke.SetInstance(fIsLoaded, container, true);
            FieldPoke.SetInstance(fAppObjClr, container, clrType);

            FieldPoke.SetInstance(_fNCLMetaAOClrTypeContainer!, meta, container);

            // Mark metadataLoaded so any LoadMetadata path is skipped.
            if (_fNCLMetaAOMetadataLoaded != null)
                FieldPoke.SetInstance(_fNCLMetaAOMetadataLoaded, meta, true);

            return meta;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine(
                $"[BcRuntime] BuildNclMetaCodeunit({id}, {clrType.Name}) failed: "
                + $"{inner.GetType().Name}: {inner.Message}");
            return null;
        }
    }
}
