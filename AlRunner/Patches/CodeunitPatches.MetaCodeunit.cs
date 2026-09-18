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
    /// <summary>
    /// Give the skeleton meta the codeunit's AL-declared <c>TestHttpRequestPolicy</c> (#2547).
    ///
    /// <para><c>NavTestExecution.TestHandleHttpClientRequest</c> reads
    /// <c>executingTestCodeUnit.MetaCodeunit.TestCodeunitHttpRequestPolicy</c> to decide what to
    /// do when a request is not served by an <c>[HttpClientHandler]</c>: throw its own
    /// unhandled-request error, or return false and let the caller send it for real. Nothing
    /// populated it here, so it read the CLR default — and <c>BlockOutboundRequests</c> is enum
    /// value 0. Every test codeunit therefore reported Block whatever its AL said, which made
    /// the property inert and the runner's own egress refusal unreachable from AL.</para>
    ///
    /// <para><b>Absent means ALLOW, not Block.</b> That is a measured service-tier verdict, not
    /// a choice: al-language corpus codeunit 60874 "Test HttpClient" declares no
    /// <c>TestHttpRequestPolicy</c>, performs a real outbound GET, and is green on real BC. If
    /// the platform default were Block it would raise
    /// <c>NavNCLTestCodeunitUnhandledHttpRequestException</c> there and could not pass. So an
    /// unset property leaves the enum at <c>AllowAllOutboundRequests</c> here, and the request
    /// travels on to the runner's own egress boundary
    /// (<c>NavHttpClient.SendAsync(DataError, HttpRequestMessage, ByRef)</c>), which refuses it
    /// as <c>external-http</c> — the same answer that codeunit has always got.</para>
    ///
    /// <para>Silent on failure by design, and it is the one place in this change where that is
    /// right: the field is a policy INPUT to BC's own decision, not a guard of ours. If the
    /// property cannot be found on some BC build, the value stays at Block, which is the
    /// conservative direction — BC refuses the request rather than passing it on. The thing
    /// that must never fail quietly is the egress rewrite itself, and that one throws.</para>
    /// </summary>
    private static void ApplyTestHttpRequestPolicy(object meta, int codeunitId)
    {
        try
        {
            var f = meta.GetType().GetField("<TestCodeunitHttpRequestPolicy>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return;

            var declared = AlRunner.Patches.RecordPatches.TryGetParsedTestHttpRequestPolicy(codeunitId);
            // The enum's own member names are the AL spellings, so Enum.Parse IS the mapping —
            // a hand-written switch would be a second spelling of BC's enum, free to drift from
            // it. An unparseable value (a property this runner has not seen, a typo the AL
            // compiler would have rejected) falls back to the same default as absent.
            object? value = null;
            if (declared != null)
            {
                try { value = Enum.Parse(f.FieldType, declared, ignoreCase: true); }
                catch (ArgumentException) { value = null; }
            }
            value ??= Enum.Parse(f.FieldType, "AllowAllOutboundRequests", ignoreCase: true);
            FieldPoke.SetInstance(f, meta, value);
        }
        catch { /* see the doc comment: Block is the conservative fallback */ }
    }

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
            if (attr.GetType().Name != "NavCodeunitOptionsAttribute") continue;
            return ReadEventManualBindingFromAttribute(attr);
        }
        return false;
    }

    /// <summary>What a wrong answer on this surface costs; shared by the reads that refuse.</summary>
    private const string ManualBindingCost =
        "the runner cannot tell a manual-binding codeunit from an automatic one; answering "
        + "false would silently unbind every manual subscriber";

    /// <summary>The AL-visible surface all three refusals on this path are serving.</summary>
    private const string ManualBindingSurface = "codeunit event binding (IsEventManualBinding)";

    /// <summary>
    /// Does this <c>[NavCodeunitOptionsAttribute]</c> instance declare manual event binding?
    ///
    /// <para>The attribute's own derived <c>IsEventManualBinding</c> property is the answer when
    /// it exists — BC computes it, so nothing here can drift from it. The fallback reads the flag
    /// out of the <c>Options</c> enum <b>by member name</b> and masks it the way BC's own property
    /// masks it, <c>(Options &amp; flag) != 0</c> rather than <c>HasFlag</c>'s all-bits test: that
    /// is literally BC's IL on 28.1.49838.53910 (Ncl.dll sha256 <c>49b11d9b…</c>). A hardcoded
    /// mask is a second spelling of BC's enum and free to drift from it — it had, both readers
    /// masking with <c>1</c>, which is <c>SingleInstance</c> (#4289).</para>
    ///
    /// <para>Trap: the fallback is unreachable while BC's attribute keeps the derived property,
    /// which is why nothing caught the mask. Measured on 28.1.49838.53910 only.</para>
    ///
    /// <para>Trap for a later editor: there are THREE ways of not getting an answer here and only
    /// one of them is a <c>false</c>. An <c>Options</c> enum declaring no <c>EventManualBinding</c>
    /// member is a read that SUCCEEDED, and <c>false</c> is what it read; an <c>Options</c> that is
    /// absent, null, or not an enum is a read that could not be performed, and refuses. Fold
    /// either side into the other and a BC shape change starts reading as a routine negative
    /// (#4319; <c>guards-need-a-third-state.md</c> § "A reflection bind that answers null is
    /// unmeasurable, not absent"; the table is in
    /// docs/codeunit-manual-binding.md#the-three-ways-of-not-getting-an-answer).</para>
    /// </summary>
    private static bool ReadEventManualBindingFromAttribute(object attr)
    {
        var t = attr.GetType();
        var isManual = t.GetProperty("IsEventManualBinding",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (isManual != null)
        {
            try { return (bool)isManual.GetValue(attr)!; } catch { }
        }
        var options = BcShape.Property(t, "Options", BcShape.AnyInstance,
            ManualBindingSurface,
            "NavCodeunitOptionsAttribute exposes no readable IsEventManualBinding property and "
            + "no Options member, so " + ManualBindingCost)
            .GetValue(attr);
        var flags = BcShape.RequiredEnum(
            options, $"{t.Name}.Options", ManualBindingSurface, ManualBindingCost);
        var enumType = flags.GetType();
        foreach (var name in Enum.GetNames(enumType))
        {
            if (name != "EventManualBinding") continue;
            return (OptionBits(flags) & OptionBits((Enum)Enum.Parse(enumType, name))) != 0;
        }
        return false;
    }

    /// <summary>
    /// The raw bits of an option flag widened to 64 — reinterpreted, never range-converted.
    ///
    /// <para>Not <c>Convert.ToInt64</c>, which raises <see cref="OverflowException"/> for a
    /// ulong-backed enum member above <see cref="long.MaxValue"/>. That throw used to be caught
    /// and answered <c>false</c>, which is a wrong answer about a value that decodes exactly
    /// rather than a gap — so this is a decode fix, not a third state (#4319). Measured: that
    /// conversion is the ONLY reachable throw the old <c>catch</c> could see, because
    /// <c>Enum.Parse</c> cannot fail for a name <c>Enum.GetNames</c> just returned. There is no
    /// <c>catch</c> here on purpose; an unexpected throw is loud, which is the right direction
    /// (<c>loud-failures.md</c>).</para>
    /// </summary>
    private static ulong OptionBits(Enum value)
        => Enum.GetUnderlyingType(value.GetType()) == typeof(ulong)
            ? Convert.ToUInt64(value)
            : unchecked((ulong)Convert.ToInt64(value));

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

    /// <summary>
    /// Resolve a codeunit's <c>NCLMetaCodeunit</c> from its AL object id alone, for callers that
    /// have an id rather than a live <c>NavCodeunit</c> — <c>NCLMetadata.GetMetaApplicationObject
    /// (ObjectType.Codeunit, …)</c> being the one that matters (#4218).
    ///
    /// <para>Observably equivalent because the meta it returns is not an answer of ours: BC reads
    /// <c>ApplicationObjectClrType</c> off it and then does its own reflection over the AL-emitted
    /// <c>Codeunit{N}</c> type — <c>NavEventPublisherReflectionHelper.GetScopeType</c> then
    /// <c>GetMethodInfoByScopeType(...).Attribute</c> — so the <c>NavEventAttribute</c> BC ends up
    /// with is the publisher's real <c>[IntegrationEvent]</c>/<c>[BusinessEvent]</c>, read from the
    /// assembly the AL compiler emitted. Same <c>ObjectType=5</c>, id and <c>NavAppGroup</c> BC's own
    /// <c>NCLMetaCodeunit</c> ctor records. Citation: corpus codeunit 60955
    /// <c>EventSubscription_Row_DescribesTheSubscription</c>, green on a real service tier.</para>
    ///
    /// <para>Trap: returning <c>null</c> here must stay a not-found rather than a fabricated meta —
    /// the caller turns it into BC's own <c>NavMetadataNotFoundException</c>, which is what
    /// <c>TryGetMetaApplicationObject</c> is written to catch. A codeunit whose CLR type this run
    /// cannot see is genuinely absent, and BC reports that truthfully as
    /// <c>ErrorOriginalApplicationObjectNotFound</c>.</para>
    /// </summary>
    internal static object? EnsureCodeunitMetaById(int codeunitId)
    {
        if (codeunitId <= 0) return null;

        EnsureMetaCodeunitReflection();
        if (_metaCodeunitReflectionFailed || _mCreateEmptyNCLMetaCodeunit == null) return null;

        var clrType = FindCodeunitTypePublic(codeunitId);
        if (clrType == null) return null;

        var meta = _navCodeunitMetaCache.GetOrAdd(codeunitId, id => BuildNclMetaCodeunit(id, clrType));
        if (meta == null) return null;

        // Same stash NavCodeunit_get_MetaCodeunit keeps, for the same reason: the
        // IsEventManualBinding hook cannot go through base.ApplicationObjectClrType.
        try { _metaToClrType.AddOrUpdate(meta, clrType); } catch { }
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

            ApplyTestHttpRequestPolicy(meta, id);

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
