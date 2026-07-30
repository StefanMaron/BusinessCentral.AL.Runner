// EventSubscriberPatches — W-8b A-prime: event-subscriber dispatch via BC's own registry.
//
// The A-base attempt (session 82e7fffc) tried to hook NCLMetaApplicationObject.IsEventSubscribed
// and the 8 OnXxxEventAsync entry points on NavTableTriggerEventHandler. Both targets are
// R2R-inlined into NavRecord.InsertAsync (and its siblings) inside Ncl.dll, so JmpHooks on the
// precode silently no-op. We pivot to populating skeleton state BC's own dispatcher reads:
//
//   1. Discovery — scan loaded assemblies for [NavEventSubscriberAttribute] methods, index by
//      (publisher table id, NavTriggerEventType ordinal).
//   2. NCLMetaTable.tableTriggerEventHandler is poked to a real NavTableTriggerEventHandler
//      instance in RecordPatches.NclMetaTableBuilder.BuildNCLMetaTable (so the field-getter
//      properties TableTriggerEventHandler / TriggerEventHandler return a non-null handler
//      even from inlined NavRecord.InsertAsync bodies).
//   3. For each (publisher,event) key in our registry, build a real NavEventSubscription via
//      BC's own 5-arg ctor and append it to NavEventScope.registeredSubscriptions (created by
//      NavTriggerEventHandler.GetEventScope(evt, EventScopeGetOption.CreateIfNotFound)).
//   4. Dispatch path is then 100% BC's own code — IsEventSubscribed → HasSubscribersForAppGroup
//      → registeredSubscriptions[] → ProcessCallToTypeAndManualSubscriptionsAsync →
//      new NavCodeunitHandle(scope, codeunitId).Target → CallEventSubscriberInternalAsync.
//
// Loud failures: subscribers we cannot match throw RunnerOutOfScopeException — never silently
// skipped (see .claude/rules/loud-failures.md).

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.EventSubscription;
using Microsoft.Dynamics.Nav.Types;
using NCLMetaTable = Microsoft.Dynamics.Nav.Runtime.NCLMetaTable;
using NCLMetaField = Microsoft.Dynamics.Nav.Runtime.NCLMetaField;

namespace AlRunner.Patches;

public static class EventSubscriberPatches
{
    private readonly record struct Key(int PublisherId, int EventTypeOrdinal);
    private readonly record struct CodeunitEventKey(int PublisherCodeunitId, string EventMethodName);

    private sealed record SubscriberHandle(
        Type CodeunitType,
        int CodeunitId,
        MethodInfo Method,
        int PublisherId,
        int EventTypeOrdinal,
        string DiagnosticName);

    // Field-scoped validate subscribers ([EventSubscriber(Table, …, 'OnAfterValidateEvent',
    // 'FieldName', …)]). Unlike Insert/Modify/Delete/Rename (table-level, dispatched from
    // NCLMetaTable.tableTriggerEventHandler), OnBefore/OnAfterValidateEvent fire from the
    // *field's own* NavEventScope (NCLMetaField.GetEventScope) — see DoInjectValidate.
    private sealed record ValidateSub(SubscriberHandle Handle, int FieldId, string FieldName);

    private static readonly object _lock = new();
    private static readonly Dictionary<Key, List<SubscriberHandle>> _byKey = new();
    private static readonly Dictionary<CodeunitEventKey, List<MethodInfo>> _byCodeunitKey = new();
    private static readonly List<ValidateSub> _validateSubs = new();

    /// <summary>
    /// Look up codeunit-event subscribers registered for a (publisherCodeunitId, eventMethodName).
    /// Called by CodeunitEventDispatcher at runtime from the Cecil-rewritten OnRunEventAsync.
    /// </summary>
    public static IReadOnlyList<MethodInfo>? GetCodeunitSubscribers(int publisherCodeunitId, string eventMethodName)
    {
        EnsureRegistryFresh();
        lock (_lock)
        {
            return _byCodeunitKey.TryGetValue(new CodeunitEventKey(publisherCodeunitId, eventMethodName), out var l) ? l : null;
        }
    }
    private static readonly HashSet<MethodInfo> _injectedSubscriberMethods = new();
    private static int _lastScannedCount = 0;
    private static bool _registered = false;
    private static bool _reflectionFailed = false;

    // Reflected BC types / members.
    private static Type? _tNavTriggerEventType;
    private static Type? _tEventScopeGetOption;
    private static Type? _tNavAppGroup;
    private static Type? _tNavEventScope;
    private static Type? _tNavEventSubscription;
    private static Type? _tNavEventSubscriberMethodInfo;
    private static Type? _tNavEventSubscriberReflectionWrapper;
    private static Type? _tNavEventSubscriptionModifiers;
    private static Type? _tNavTableTriggerEventHandler;
    private static Type? _tNCLMetaTable;
    private static MethodInfo? _miGetEventScope;          // NavTriggerEventHandler.GetEventScope(2-arg)
    private static FieldInfo? _fRegisteredSubscriptions;  // NavEventScope.registeredSubscriptions
    private static FieldInfo? _fTableTriggerEventHandler; // NCLMetaTable.tableTriggerEventHandler
    private static ConstructorInfo? _ciNavEventSubscription;
    private static ConstructorInfo? _ciNavEventSubscriberMethodInfo;
    private static ConstructorInfo? _ciNavEventSubscriberReflectionWrapper;
    private static ConstructorInfo? _ciNavTableTriggerEventHandler;
    private static object? _navAppGroupBaseGroup;
    private static object? _emptyModifiers;

    private static Func<int, object?>? _publisherLookup;

    /// <summary>
    /// Resolve BC types once and remember the publisher → NCLMetaTable lookup callback
    /// (typically the closure over RecordPatches._metaTableCache).
    /// </summary>
    public static void Register(Assembly navNcl)
    {
        if (_registered) return;
        _registered = true;
        try { EnsureReflection(navNcl); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Subscribers] Register failed (dispatch disabled): " +
                $"{ex.GetType().Name}: {ex.Message}");
            _reflectionFailed = true;
        }
    }

    /// <summary>
    /// Build a fresh NavTableTriggerEventHandler instance via its parameterless internal ctor —
    /// used by RecordPatches.NclMetaTableBuilder to populate NCLMetaTable.tableTriggerEventHandler.
    /// Returns null if reflection setup failed.
    /// </summary>
    public static object? CreateTableTriggerEventHandler()
    {
        if (_reflectionFailed) return null;
        // Self-initialize rather than depend on Register() having been called first.
        // The platform tables (2000000xxx) are built during runtime bring-up, BEFORE
        // Register runs, so an ordering dependency here silently handed every one of them
        // a null tableTriggerEventHandler — and NavRecord.InsertAsync calls
        // metaTable.TableTriggerEventHandler.OnBeforeInsertEventAsync unconditionally once
        // IsEventSubscribed says yes, so the null surfaced as a bare NRE deep inside BC.
        // BC itself never leaves this field null (NCLMetaTable.Populate always assigns one).
        if (_ciNavTableTriggerEventHandler == null)
        {
            var navNcl = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
            if (navNcl == null) return null;
            Register(navNcl);
        }
        if (_ciNavTableTriggerEventHandler == null) return null;
        try { return _ciNavTableTriggerEventHandler.Invoke(null); }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[Subscribers] NavTableTriggerEventHandler ctor failed: " +
                $"{inner.GetType().Name}: {inner.Message}");
            return null;
        }
    }

    /// <summary>
    /// Inject all discovered subscribers into the per-table NavEventScope objects.
    /// Idempotent — each subscriber MethodInfo is added at most once.
    /// Lookup callback returns the NCLMetaTable for a given publisher table id, or null.
    /// </summary>
    public static void InjectAll(Func<int, object?> getNclMetaTable)
    {
        _publisherLookup = getNclMetaTable;
        InjectAllUsingStoredLookup();
    }

    /// <summary>
    /// Re-run injection using the lookup callback installed by a prior <see cref="InjectAll"/>.
    /// Called by TestExecutor before each test bundle runs so subscribers in AL assemblies
    /// loaded after PopulateNclMetadataCache (e.g. the test codeunit's containing assembly)
    /// get wired up too.
    /// </summary>
    public static void InjectAllUsingStoredLookup()
    {
        if (_publisherLookup == null) return;
        DoInject(_publisherLookup);
        DoInjectValidate(_publisherLookup);
        SeedCodeunitEventScopeSentinels();
    }

    private static readonly HashSet<Type> _seededScopeTypes = new();
    private static readonly Dictionary<int, Type?> _codeunitTypeCache = new();
    private static object? _sentinelNavEventScope;

    /// <summary>
    /// For each (publisherCodeunitId, eventMethodName) with subscribers, find the publisher's
    /// <c>Codeunit&lt;N&gt;+&lt;EventName&gt;_Scope</c> and seed its static <c>γeventScope</c>
    /// field with a structurally-valid sentinel <see cref="NavEventScope"/>. The sentinel has
    /// a non-null <c>lockObject</c> and an empty (not null) <c>registeredSubscriptions</c> array
    /// — JIT-inlined code in BC's machinery can read both safely without crashing.
    ///
    /// Bypasses the AL publisher's early-exit
    /// <c>if (γeventScope == null &amp;&amp; !recorder) return</c> without forcing the recorder
    /// (which has widespread cascading side effects we cannot satisfy on the skeleton runtime).
    /// </summary>
    private static void SeedCodeunitEventScopeSentinels()
    {
        if (_byCodeunitKey.Count == 0) return;
        if (_tNavEventScope == null || _tNavEventSubscription == null) return;

        if (_sentinelNavEventScope == null)
        {
            try
            {
                _sentinelNavEventScope = System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(_tNavEventScope);
                var fLock = _tNavEventScope.GetField("lockObject",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (fLock != null) FieldPoke.SetInstance(fLock, _sentinelNavEventScope, new object());
                // Empty array (not null) — BC's HasSubscribers reads .Length safely; any
                // JIT-inlined Length read also returns 0 without segfaulting on a null array.
                var emptySubs = Array.CreateInstance(_tNavEventSubscription, 0);
                if (_fRegisteredSubscriptions != null)
                    FieldPoke.SetInstance(_fRegisteredSubscriptions, _sentinelNavEventScope, emptySubs);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Subscribers] sentinel NavEventScope build failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }
        }

        int seeded = 0, missing = 0;
        bool diagLogged = false;
        lock (_lock)
        {
            foreach (var key in _byCodeunitKey.Keys)
            {
                var cuType = FindCodeunitClrType(key.PublisherCodeunitId);
                if (cuType == null) { missing++; if (!diagLogged) { diagLogged = true; Console.Error.WriteLine($"[Subscribers] seed-miss: Codeunit{key.PublisherCodeunitId} type not found"); } continue; }
                var scopeType = cuType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(t => t.Name == key.EventMethodName + "_Scope");
                if (scopeType == null) { missing++; continue; }
                if (_seededScopeTypes.Contains(scopeType)) continue;
                // Match the Greek-gamma field by suffix — the IL gamma codepoint differs from
                // a C# source-literal "γ" so GetField("γeventScope") returns null. EndsWith works.
                var fld = scopeType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(f => f.Name.EndsWith("eventScope", StringComparison.Ordinal) && f.FieldType == _tNavEventScope);
                if (fld == null) { missing++; continue; }
                try { fld.SetValue(null, _sentinelNavEventScope); _seededScopeTypes.Add(scopeType); seeded++; }
                catch (Exception ex) { Console.Error.WriteLine($"[Subscribers] failed seed on {scopeType.FullName}: {ex.Message}"); missing++; }
            }
        }
        if (seeded > 0)
            Console.Error.WriteLine($"[Subscribers] γeventScope seeded: seeded={seeded} missing={missing} total-keys={_byCodeunitKey.Count}");
    }

    /// <summary>
    /// Drop the subscriber registries and per-bundle codeunit-type cache so a server
    /// reload of the same-identity bundle rebuilds them from the freshly-emitted
    /// assembly instead of accumulating (or double-firing) the previous run's
    /// subscribers. Resetting <c>_lastScannedCount</c> forces a full re-scan; the
    /// stale previous bundle assembly is then skipped via
    /// <see cref="BcRuntime.IsStaleBundleAssembly"/>. The installed injection hook
    /// (<c>_registered</c>) and any reflection-failure latch are preserved.
    /// </summary>
    public static void ResetForReload()
    {
        lock (_lock)
        {
            _byKey.Clear();
            _byCodeunitKey.Clear();
            _validateSubs.Clear();
            _codeunitTypeCache.Clear();
            _injectedSubscriberMethods.Clear();
            _seededScopeTypes.Clear();
            _lastScannedCount = 0;
        }
    }

    private static Type? FindCodeunitClrType(int codeunitId)
    {
        if (_codeunitTypeCache.TryGetValue(codeunitId, out var cached)) return cached;
        var name = "Codeunit" + codeunitId;
        Type? found = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var n = asm.GetName().Name ?? "";
            if (n.StartsWith("System.") || n.StartsWith("Microsoft.Extensions.")
                || n == "netstandard" || n == "mscorlib"
                || n == "AlRunner" || n == "Runner"
                || n.StartsWith("Microsoft.CodeAnalysis")) continue;
            // Skip a previous bundle assembly still loaded after a server reload.
            if (BcRuntime.IsStaleBundleAssembly(asm)) continue;
            try { var t = asm.GetType("Microsoft.Dynamics.Nav.BusinessApplication." + name); if (t != null) { found = t; break; } }
            catch { }
        }
        _codeunitTypeCache[codeunitId] = found;
        return found;
    }

    private static void DoInject(Func<int, object?> getNclMetaTable)
    {
        if (_reflectionFailed) return;
        EnsureRegistryFresh();
        if (_byKey.Count == 0) return;
        if (_navAppGroupBaseGroup == null) return;

        int injected = 0, failed = 0, skipped = 0;
        lock (_lock)
        {
            foreach (var kv in _byKey)
            {
                int publisherId = kv.Key.PublisherId;
                int ord = kv.Key.EventTypeOrdinal;

                object? metaTable;
                try { metaTable = getNclMetaTable(publisherId); }
                catch { metaTable = null; }
                if (metaTable == null)
                {
                    // Publisher table not yet built — will retry on next InjectAll pass.
                    foreach (var s in kv.Value) if (!_injectedSubscriberMethods.Contains(s.Method)) skipped++;
                    continue;
                }

                object? handler;
                try { handler = _fTableTriggerEventHandler!.GetValue(metaTable); }
                catch { handler = null; }
                if (handler == null)
                {
                    foreach (var s in kv.Value) if (!_injectedSubscriberMethods.Contains(s.Method)) skipped++;
                    continue;
                }

                var ordEnum = Enum.ToObject(_tNavTriggerEventType!, ord);
                var createIfNotFound = Enum.ToObject(_tEventScopeGetOption!, 1); // CreateIfNotFound
                object? scope;
                try { scope = _miGetEventScope!.Invoke(handler, new object?[] { ordEnum, createIfNotFound }); }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                    Console.Error.WriteLine($"[Subscribers] GetEventScope({publisherId},{ord}) failed: " +
                        $"{inner.GetType().Name}: {inner.Message}");
                    failed += kv.Value.Count;
                    continue;
                }
                if (scope == null)
                {
                    failed += kv.Value.Count;
                    continue;
                }

                var existing = (Array?)_fRegisteredSubscriptions!.GetValue(scope);
                var newOnes = new List<object>();
                foreach (var sub in kv.Value)
                {
                    if (_injectedSubscriberMethods.Contains(sub.Method)) continue;
                    object? subscription;
                    try { subscription = BuildSubscription(sub); }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                        Console.Error.WriteLine($"[Subscribers] BuildSubscription failed for " +
                            $"{sub.DiagnosticName}: {inner.GetType().Name}: {inner.Message}");
                        failed++;
                        _injectedSubscriberMethods.Add(sub.Method); // don't retry
                        continue;
                    }
                    if (subscription == null) { failed++; _injectedSubscriberMethods.Add(sub.Method); continue; }
                    newOnes.Add(subscription);
                    _injectedSubscriberMethods.Add(sub.Method);
                    injected++;
                }
                if (newOnes.Count == 0) continue;

                int oldLen = existing?.Length ?? 0;
                var merged = Array.CreateInstance(_tNavEventSubscription!, oldLen + newOnes.Count);
                if (existing != null && oldLen > 0) Array.Copy(existing, 0, merged, 0, oldLen);
                for (int i = 0; i < newOnes.Count; i++) merged.SetValue(newOnes[i], oldLen + i);
                FieldPoke.SetInstance(_fRegisteredSubscriptions, scope, merged);
            }
        }

        if (injected > 0 || failed > 0)
            Console.Error.WriteLine($"[Subscribers] inject: injected={injected} failed={failed} " +
                $"skipped-no-publisher={skipped} keys={_byKey.Count}");
    }

    /// <summary>
    /// Inject field-scoped validate subscribers (OnBefore/OnAfterValidateEvent). These are
    /// dispatched by BC's own NavRecord validate path:
    ///   if (metaField.IsEventSubscribed(OnAfterValidateEvent, appGroup))
    ///       NavTableTriggerEventHandler.FireOnValidateEvent(OnAfterValidateEvent, this, metaField)
    ///   → metaField.GetEventScope(triggerEventType).CheckAndFireTriggerEventsAsync(...)
    /// so the field's *own* NavEventScope is the per-field filter (the fire path filters
    /// only by app group, never re-checks the field). We therefore resolve the subscriber's
    /// target field on the publisher table and append the subscription to that field's scope.
    /// </summary>
    private static void DoInjectValidate(Func<int, object?> getNclMetaTable)
    {
        if (_validateSubs.Count == 0) return;
        if (_navAppGroupBaseGroup == null) return;

        int injected = 0, failed = 0, skipped = 0;
        lock (_lock)
        {
            foreach (var vs in _validateSubs)
            {
                if (_injectedSubscriberMethods.Contains(vs.Handle.Method)) continue;

                NCLMetaTable? metaTable;
                try { metaTable = getNclMetaTable(vs.Handle.PublisherId) as NCLMetaTable; }
                catch { metaTable = null; }
                if (metaTable == null) { skipped++; continue; } // publisher table not built yet — retry / lazy-inject

                TryInjectOneValidateSub(vs, metaTable, ref injected, ref failed);
            }
        }

        if (injected > 0 || failed > 0)
            Console.Error.WriteLine($"[Subscribers] validate-inject: injected={injected} " +
                $"failed={failed} skipped-no-publisher={skipped} subs={_validateSubs.Count}");
    }

    /// <summary>
    /// Inject any not-yet-injected field-validate subscribers that target <paramref name="tableId"/>
    /// onto the supplied (freshly built) NCLMetaTable. Called from BuildNCLMetaTable so a subscriber
    /// on a not-yet-built publisher table — e.g. an ISV subscribing to a precompiled BaseApp table's
    /// OnAfterValidateEvent — is wired onto the very metatable instance the runtime Record uses, at
    /// the moment that table is first built (no eager startup building, which perturbs unrelated
    /// setup). Idempotent via <c>_injectedSubscriberMethods</c>.
    /// </summary>
    public static void InjectValidateSubsForTable(int tableId, object metaTableObj)
    {
        if (_reflectionFailed || _navAppGroupBaseGroup == null) return;
        if (metaTableObj is not NCLMetaTable metaTable) return;
        // Ensure discovery has run so _validateSubs is populated (it may not have been if this
        // table is built before the first bulk injection pass).
        try { EnsureRegistryFresh(); } catch { }
        if (_validateSubs.Count == 0) return;

        lock (_lock)
        {
            int injected = 0, failed = 0;
            foreach (var vs in _validateSubs)
            {
                if (vs.Handle.PublisherId != tableId) continue;
                if (_injectedSubscriberMethods.Contains(vs.Handle.Method)) continue;
                TryInjectOneValidateSub(vs, metaTable, ref injected, ref failed);
            }
            if (injected > 0 || failed > 0)
                Console.Error.WriteLine($"[Subscribers] validate-inject (table {tableId}, lazy): " +
                    $"injected={injected} failed={failed}");
        }
    }

    /// <summary>Inject one validate subscriber onto its target field's event scope on
    /// <paramref name="metaTable"/>. Caller holds <c>_lock</c>. Marks the method injected so it is
    /// not double-registered (which would fire the subscriber twice).</summary>
    private static void TryInjectOneValidateSub(ValidateSub vs, NCLMetaTable metaTable,
        ref int injected, ref int failed)
    {
        NCLMetaField? metaField = ResolveValidateField(metaTable, vs);
        if (metaField == null)
        {
            // Loud: the subscriber names a field we cannot resolve on the publisher table.
            Console.Error.WriteLine($"[Subscribers] validate target field not found: {vs.Handle.DiagnosticName}");
            _injectedSubscriberMethods.Add(vs.Handle.Method); // don't retry forever
            failed++;
            return;
        }
        EnsureFieldEventTriggerData(metaField);

        object? scope;
        try
        {
            scope = metaField.GetEventScope(
                (NavTriggerEventType)vs.Handle.EventTypeOrdinal,
                EventScopeGetOption.CreateIfNotFound);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[Subscribers] field GetEventScope failed for " +
                $"{vs.Handle.DiagnosticName}: {inner.GetType().Name}: {inner.Message}");
            failed++;
            return;
        }
        if (scope == null) { failed++; return; }

        object? subscription;
        try { subscription = BuildSubscription(vs.Handle); }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine($"[Subscribers] BuildSubscription failed for " +
                $"{vs.Handle.DiagnosticName}: {inner.GetType().Name}: {inner.Message}");
            failed++;
            _injectedSubscriberMethods.Add(vs.Handle.Method);
            return;
        }
        if (subscription == null) { failed++; _injectedSubscriberMethods.Add(vs.Handle.Method); return; }

        AppendSubscriptionToScope(scope, subscription);
        _injectedSubscriberMethods.Add(vs.Handle.Method);
        injected++;
    }

    /// <summary>Resolve a validate subscriber's target field on the publisher table — by
    /// field name (the common AL form) first, falling back to the numeric field id.</summary>
    private static NCLMetaField? ResolveValidateField(NCLMetaTable metaTable, ValidateSub vs)
    {
        if (!string.IsNullOrEmpty(vs.FieldName)
            && metaTable.TryGetFieldByName(vs.FieldName, out var byName) && byName != null)
            return byName;
        if (vs.FieldId != 0 && metaTable.TryGetFieldByNo(vs.FieldId, out var byNo) && byNo != null)
            return byNo;
        return null;
    }

    private static FieldInfo? _fMetaFieldEventTriggerDataBacking;

    /// <summary>NCLMetaField.EventTriggerDataValue is a get-only auto-prop; BC sets it lazily
    /// when a field has an OnValidate/OnLookup trigger. A validate subscriber may target a
    /// field with no trigger, so ensure the holder exists before GetEventScope reads it.</summary>
    private static void EnsureFieldEventTriggerData(NCLMetaField metaField)
    {
        if (metaField.EventTriggerDataValue != null) return;
        _fMetaFieldEventTriggerDataBacking ??= typeof(NCLMetaField).GetField(
            "<EventTriggerDataValue>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (_fMetaFieldEventTriggerDataBacking == null) return;
        var etd = new NCLMetaField.EventTriggerData();
        FieldPoke.SetInstance(_fMetaFieldEventTriggerDataBacking, metaField, etd);
    }

    /// <summary>Append one NavEventSubscription to a NavEventScope.registeredSubscriptions array.</summary>
    private static void AppendSubscriptionToScope(object scope, object subscription)
    {
        var existing = (Array?)_fRegisteredSubscriptions!.GetValue(scope);
        int oldLen = existing?.Length ?? 0;
        var merged = Array.CreateInstance(_tNavEventSubscription!, oldLen + 1);
        if (existing != null && oldLen > 0) Array.Copy(existing, 0, merged, 0, oldLen);
        merged.SetValue(subscription, oldLen);
        FieldPoke.SetInstance(_fRegisteredSubscriptions, scope, merged);
    }

    /// <summary>Read the validate subscriber's target field (name + id) from its
    /// NavEventSubscriberAttribute.</summary>
    private static void ReadFieldTarget(object attr, out int fieldId, out string fieldName)
    {
        fieldId = 0; fieldName = "";
        var t = attr.GetType();
        try { fieldId = (int)(t.GetProperty("TargetFieldId")?.GetValue(attr) ?? 0); } catch { }
        try { fieldName = (string?)t.GetProperty("TargetFieldName")?.GetValue(attr) ?? ""; } catch { }
    }

    /// <summary>
    /// Discovery: walk loaded assemblies for [NavEventSubscriberAttribute] methods, index
    /// by (publisher id, NavTriggerEventType ordinal). Incremental — only re-scans when the
    /// assembly count grows.
    /// </summary>
    private static void EnsureRegistryFresh()
    {
        var asms = AppDomain.CurrentDomain.GetAssemblies();
        if (asms.Length == _lastScannedCount) return;
        lock (_lock)
        {
            asms = AppDomain.CurrentDomain.GetAssemblies();
            if (asms.Length == _lastScannedCount) return;

            int added = 0;
            int scannedAttrs = 0;
            foreach (var asm in asms)
            {
                var name = asm.GetName().Name ?? "";
                if (name.StartsWith("System.") || name.StartsWith("Microsoft.Extensions.")
                    || name.StartsWith("Microsoft.Dynamics.Nav.") || name == "netstandard"
                    || name == "mscorlib" || name == "Microsoft.CodeAnalysis"
                    || name.StartsWith("Microsoft.CodeAnalysis.")
                    || name == "AlRunner" || name == "Runner") continue;
                // Skip a previous bundle assembly still loaded after a server reload —
                // otherwise its [EventSubscriber] codeunits re-register alongside the
                // new ones and events fire twice. No-op in normal one-shot mode.
                if (BcRuntime.IsStaleBundleAssembly(asm)) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t == null) continue;
                    // Only AL codeunits can host [NavEventSubscriberAttribute] methods. The emitted
                    // test assembly contains thousands of generated types (Record<N>, Table<N>,
                    // Page<N>, Enum<N>, ...) that are guaranteed to have no subscribers — walking
                    // their methods + reading custom attributes on each was the bulk of the cost
                    // (this scan was ~35% inclusive in the bucket-1 bundled profile).
                    if (!t.Name.StartsWith("Codeunit", StringComparison.Ordinal)) continue;
                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                  | BindingFlags.Instance | BindingFlags.Static); }
                    catch { continue; }
                    int codeunitId = -1; // lazy: only read when first subscriber found
                    foreach (var m in methods)
                    {
                        object? attr;
                        try
                        {
                            attr = m.GetCustomAttributes(inherit: false)
                                .FirstOrDefault(a => a.GetType().Name == "NavEventSubscriberAttribute");
                        }
                        catch { continue; }
                        if (attr == null) continue;
                        if (codeunitId < 0) codeunitId = TryReadCodeunitId(t);
                        scannedAttrs++;
                        if (!TryReadAttribute(attr, out int publisherObjType,
                                              out int publisherId, out string methodName))
                        { Console.Error.WriteLine($"[Subscribers] could not read attr on {t.Name}.{m.Name}"); continue; }
                        if (publisherObjType == 1) // Table → existing trigger path
                        {
                            int ord = ResolveEventOrdinalFromName(methodName);
                            if (ord == 9 || ord == 10) // OnBefore/OnAfterValidateEvent — field-scoped
                            {
                                if (_validateSubs.Any(v => v.Handle.Method == m)) continue;
                                ReadFieldTarget(attr, out int vFieldId, out string vFieldName);
                                var vHandle = new SubscriberHandle(t, codeunitId, m, publisherId, ord,
                                    $"{t.Name}.{m.Name} → Table {publisherId}/{methodName}('{vFieldName}')");
                                _validateSubs.Add(new ValidateSub(vHandle, vFieldId, vFieldName));
                                added++;
                                continue;
                            }
                            if (ord == 0) continue;
                            var key = new Key(publisherId, ord);
                            if (!_byKey.TryGetValue(key, out var lst))
                                _byKey[key] = lst = new List<SubscriberHandle>();
                            if (lst.Any(h => h.Method == m)) continue;
                            lst.Add(new SubscriberHandle(t, codeunitId, m, publisherId, ord,
                                $"{t.Name}.{m.Name} → Table {publisherId}/{methodName}"));
                            added++;
                        }
                        else if (publisherObjType == 5) // Codeunit → new universal dispatch path
                        {
                            var ckey = new CodeunitEventKey(publisherId, methodName);
                            if (!_byCodeunitKey.TryGetValue(ckey, out var clst))
                                _byCodeunitKey[ckey] = clst = new List<MethodInfo>();
                            if (clst.Contains(m)) continue;
                            clst.Add(m);
                            added++;
                        }
                    }
                }
            }
            _lastScannedCount = asms.Length;
            int total = _byKey.Values.Sum(v => v.Count);
            if (added > 0 || scannedAttrs == 0)
                Console.Error.WriteLine(
                    $"[Subscribers] registered {added} new (total {total}) " +
                    $"across {_byKey.Count} publisher-event keys (scanned-attrs={scannedAttrs})");
        }
    }

    private static bool TryReadAttribute(object attr, out int publisherType,
                                         out int publisherId, out string methodName)
    {
        publisherType = 0; publisherId = 0; methodName = "";
        var t = attr.GetType();
        var targetObjectIdProp = t.GetProperty("TargetObjectId", BindingFlags.Public | BindingFlags.Instance);
        var methodNameProp = t.GetProperty("TargetMethodName", BindingFlags.Public | BindingFlags.Instance);
        if (targetObjectIdProp == null || methodNameProp == null) return false;
        var oid = targetObjectIdProp.GetValue(attr);
        if (oid == null) return false;
        var oidT = oid.GetType();
        var otProp = oidT.GetProperty("ObjectType", BindingFlags.Public | BindingFlags.Instance);
        var onProp = oidT.GetProperty("ObjectNumber", BindingFlags.Public | BindingFlags.Instance);
        if (otProp == null || onProp == null) return false;
        publisherType = (int)(otProp.GetValue(oid) ?? 0);
        publisherId = (int)(onProp.GetValue(oid) ?? 0);
        methodName = (string?)methodNameProp.GetValue(attr) ?? "";
        return true;
    }

    private static int TryReadCodeunitId(Type clrType)
    {
        // Use CustomAttributeData (metadata-only) instead of GetCustomAttributes to avoid
        // CustomAttributeFormatException on types whose IL references attribute properties
        // we don't carry (e.g. [JsonObject(MemberSerialization=...)] in some BC types).
        IList<CustomAttributeData> attrs;
        try { attrs = CustomAttributeData.GetCustomAttributes(clrType); }
        catch { return 0; }
        foreach (var ad in attrs)
        {
            if (ad.AttributeType.Name != "ApplicationObjectIdAttribute") continue;
            // ctor signature varies; we want the ApplicationObjectId with ObjectNumber.
            // Easiest path: materialize just this one attribute.
            try
            {
                var attr = clrType.GetCustomAttributes(ad.AttributeType, inherit: false).FirstOrDefault();
                if (attr == null) return 0;
                var prop = attr.GetType().GetProperty("ApplicationObjectId",
                    BindingFlags.Public | BindingFlags.Instance);
                var aoid = prop?.GetValue(attr);
                if (aoid == null) return 0;
                var on = aoid.GetType().GetProperty("ObjectNumber",
                    BindingFlags.Public | BindingFlags.Instance);
                return (int)(on?.GetValue(aoid) ?? 0);
            }
            catch { return 0; }
        }
        return 0;
    }

    private static int ResolveEventOrdinalFromName(string name) => name switch
    {
        "OnBeforeInsertEvent" => 1,
        "OnAfterInsertEvent"  => 2,
        "OnBeforeModifyEvent" => 3,
        "OnAfterModifyEvent"  => 4,
        "OnBeforeDeleteEvent" => 5,
        "OnAfterDeleteEvent"  => 6,
        "OnBeforeRenameEvent" => 7,
        "OnAfterRenameEvent"  => 8,
        "OnBeforeValidateEvent" => 9,
        "OnAfterValidateEvent"  => 10,
        _ => 0,
    };

    private static object? BuildSubscription(SubscriberHandle sub)
    {
        // NavEventSubscriberMethodInfo(MethodInfo)
        var methodInfoObj = _ciNavEventSubscriberMethodInfo!.Invoke(new object?[] { sub.Method });
        // Replace the captured NavEventSubscriberAttribute with a zeroed copy (no
        // SkipOnMissing{License,Permission}) so BC's SkipCallDueToLackOfPermissions
        // short-circuits — accessing subscriberInstance.Session.Permissions / Company
        // would NRE on our skeleton session.
        ReplaceAttributeWithZeroedCopy(methodInfoObj!);
        // INavEventSubscriber adapter — codeunit identity points BC's dispatcher at the right
        // codeunit handle when it calls `new NavCodeunitHandle(scope, ObjectNumber).Target`.
        int codeunitId = sub.CodeunitId != 0 ? sub.CodeunitId : ExtractCodeunitIdFromTypeName(sub.CodeunitType);
        if (codeunitId == 0)
            throw new RunnerOutOfScopeException(
                $"Subscriber {sub.DiagnosticName}",
                $"could not determine codeunit ID from {sub.CodeunitType.FullName} — " +
                "missing [ApplicationObjectIdAttribute] and type name not Codeunit<N>");
        var subscriber = new AlEventSubscriberAdapter(sub.CodeunitType, codeunitId);
        // NavEventSubscription(subscriber, methodInfo, appGroup, modifiers, memberId)
        return _ciNavEventSubscription!.Invoke(new object?[] {
            subscriber, methodInfoObj, _navAppGroupBaseGroup, _emptyModifiers, 0
        });
    }

    /// <summary>
    /// Replace NavEventSubscriberMethodInfo.Attribute with a copy that has
    /// EventSubscriberCallOptions=0 (no SkipOnMissingLicense / SkipOnMissingPermission).
    /// The AL compiler always emits both flags as true, which would force BC to read
    /// subscriberInstance.Session.Permissions — null on the skeleton session and NREs.
    /// </summary>
    private static void ReplaceAttributeWithZeroedCopy(object methodInfoObj)
    {
        var miType = methodInfoObj.GetType();
        var attrProp = miType.GetProperty("Attribute", BindingFlags.Public | BindingFlags.Instance);
        var original = attrProp?.GetValue(methodInfoObj);
        if (original == null) return;
        var oType = original.GetType();
        var targetObjectId = oType.GetProperty("TargetObjectId")!.GetValue(original)!;
        var oidT = targetObjectId.GetType();
        var ot = (int)oidT.GetProperty("ObjectType")!.GetValue(targetObjectId)!;
        var on = (int)oidT.GetProperty("ObjectNumber")!.GetValue(targetObjectId)!;
        var methodName = (string)oType.GetProperty("TargetMethodName")!.GetValue(original)!;
        var memberId = (int)oType.GetProperty("MemberId")!.GetValue(original)!;
        // Preserve the field target — for field-based events (OnBefore/OnAfterValidateEvent) the
        // NavEventSubscription ctor resolves the publisher field from these and, if it can't,
        // returns early with ErrorFieldNotFound leaving SubscriberParameters null → an NRE later
        // in TriggerPrepareParametersCallBack. Zeroing the field name (as the original code did)
        // was harmless for table-level Insert/Modify/… events but broke validate-event dispatch.
        var fieldName = (string?)oType.GetProperty("TargetFieldName")!.GetValue(original) ?? "";
        var fieldId = (int)(oType.GetProperty("TargetFieldId")?.GetValue(original) ?? 0);
        object? replacement = null;
        if (!string.IsNullOrEmpty(fieldName))
        {
            // (ObjectType, int objNo, string method, int memberId, string fieldName, options)
            var ctor = oType.GetConstructors().FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 6 && p[3].ParameterType == typeof(int)
                    && p[4].ParameterType == typeof(string);
            });
            if (ctor != null)
            {
                var zeroOpts = Enum.ToObject(ctor.GetParameters()[5].ParameterType, 0);
                replacement = ctor.Invoke(new object?[] {
                    Enum.ToObject(typeof(ObjectType), ot), on, methodName, memberId, fieldName, zeroOpts
                });
            }
        }
        else if (fieldId != 0)
        {
            // (ObjectType, int objNo, string method, int memberId, int fieldId, options)
            var ctor = oType.GetConstructors().FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 6 && p[3].ParameterType == typeof(int)
                    && p[4].ParameterType == typeof(int);
            });
            if (ctor != null)
            {
                var zeroOpts = Enum.ToObject(ctor.GetParameters()[5].ParameterType, 0);
                replacement = ctor.Invoke(new object?[] {
                    Enum.ToObject(typeof(ObjectType), ot), on, methodName, memberId, fieldId, zeroOpts
                });
            }
        }
        else
        {
            // No field target (table-level Insert/Modify/Delete/Rename event).
            var ctor = oType.GetConstructors().FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 6 && p[3].ParameterType == typeof(int)
                    && p[4].ParameterType == typeof(string);
            });
            if (ctor != null)
            {
                var zeroOpts = Enum.ToObject(ctor.GetParameters()[5].ParameterType, 0);
                replacement = ctor.Invoke(new object?[] {
                    Enum.ToObject(typeof(ObjectType), ot), on, methodName, memberId, "", zeroOpts
                });
            }
        }
        if (replacement == null) return;
        // Field-poke the auto-prop backing field.
        var backing = miType.GetField("<Attribute>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (backing != null)
            FieldPoke.SetInstance(backing, methodInfoObj, replacement);
    }

    private static int ExtractCodeunitIdFromTypeName(Type t)
    {
        var n = t.Name;
        if (n.StartsWith("Codeunit", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(n.AsSpan("Codeunit".Length), out var id))
            return id;
        return 0;
    }

    private static void EnsureReflection(Assembly navNcl)
    {
        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types")
            ?? throw new InvalidOperationException("Microsoft.Dynamics.Nav.Types not loaded");

        _tNavTriggerEventType = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.NavTriggerEventType")
            ?? throw new InvalidOperationException("NavTriggerEventType not found");

        _tNavAppGroup = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.Apps.NavAppGroup")
            ?? throw new InvalidOperationException("NavAppGroup not found");
        _tNavEventScope = navNcl.GetType("Microsoft.Dynamics.Nav.EventSubscription.NavEventScope")
            ?? throw new InvalidOperationException("NavEventScope not found");
        _tNavEventSubscription = navNcl.GetType("Microsoft.Dynamics.Nav.EventSubscription.NavEventSubscription")
            ?? throw new InvalidOperationException("NavEventSubscription not found");
        _tNavEventSubscriberMethodInfo = navNcl.GetType("Microsoft.Dynamics.Nav.EventSubscription.NavEventSubscriberMethodInfo")
            ?? throw new InvalidOperationException("NavEventSubscriberMethodInfo not found");
        _tNavEventSubscriberReflectionWrapper = navNcl.GetType("Microsoft.Dynamics.Nav.EventSubscription.NavEventSubscriberReflectionWrapper")
            ?? throw new InvalidOperationException("NavEventSubscriberReflectionWrapper not found");
        _tNavEventSubscriptionModifiers = navNcl.GetType("Microsoft.Dynamics.Nav.EventSubscription.NavEventSubscriptionModifiers")
            ?? throw new InvalidOperationException("NavEventSubscriptionModifiers not found");
        _tNavTableTriggerEventHandler = navNcl.GetType("Microsoft.Dynamics.Nav.EventSubscription.NavTableTriggerEventHandler")
            ?? throw new InvalidOperationException("NavTableTriggerEventHandler not found");
        _tNCLMetaTable = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable")
            ?? throw new InvalidOperationException("NCLMetaTable not found");
        _tEventScopeGetOption = navNcl.GetType("Microsoft.Dynamics.Nav.EventSubscription.EventScopeGetOption")
            ?? throw new InvalidOperationException("EventScopeGetOption not found");

        var tNavTriggerEventHandler = navNcl.GetType("Microsoft.Dynamics.Nav.EventSubscription.NavTriggerEventHandler")
            ?? throw new InvalidOperationException("NavTriggerEventHandler not found");
        _miGetEventScope = tNavTriggerEventHandler.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "GetEventScope" && m.GetParameters().Length == 2
                                  && m.GetParameters()[0].ParameterType == _tNavTriggerEventType)
            ?? throw new InvalidOperationException("NavTriggerEventHandler.GetEventScope(2-arg) not found");

        _fRegisteredSubscriptions = _tNavEventScope.GetField("registeredSubscriptions",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("NavEventScope.registeredSubscriptions not found");
        _fTableTriggerEventHandler = _tNCLMetaTable.GetField("tableTriggerEventHandler",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("NCLMetaTable.tableTriggerEventHandler not found");

        _ciNavTableTriggerEventHandler = _tNavTableTriggerEventHandler
            .GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException("NavTableTriggerEventHandler parameterless ctor not found");

        _ciNavEventSubscription = _tNavEventSubscription.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 5)
            ?? throw new InvalidOperationException("NavEventSubscription 5-arg ctor not found");
        _ciNavEventSubscriberMethodInfo = _tNavEventSubscriberMethodInfo.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1)
            ?? throw new InvalidOperationException("NavEventSubscriberMethodInfo(MethodInfo) ctor not found");
        _ciNavEventSubscriberReflectionWrapper = _tNavEventSubscriberReflectionWrapper.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1)
            ?? throw new InvalidOperationException("NavEventSubscriberReflectionWrapper(Type) ctor not found");

        _navAppGroupBaseGroup = _tNavAppGroup.GetProperty("BaseGroup",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? _tNavAppGroup.GetField("BaseGroup",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (_navAppGroupBaseGroup == null)
            throw new InvalidOperationException("NavAppGroup.BaseGroup not resolvable");

        // Empty NavEventSubscriptionModifiers — only consulted on the non-table branch, but the
        // ctor needs a non-null value.
        var modCtor = _tNavEventSubscriptionModifiers.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .First();
        var modArgs = modCtor.GetParameters().Select(p =>
            (object)Array.CreateInstance(p.ParameterType.GenericTypeArguments[0], 0)).ToArray();
        _emptyModifiers = modCtor.Invoke(modArgs);
    }

    /// <summary>
    /// Adapter implementing <see cref="INavEventSubscriber"/>. The publisher dispatch path only
    /// reads ApplicationObjectId.ObjectNumber + IsEventManualBinding off the subscriber object
    /// (other members are consumed by manual-binding / sender-validation paths we don't take).
    /// </summary>
    internal sealed class AlEventSubscriberAdapter : INavEventSubscriber
    {
        private readonly NavEventSubscriberReflectionWrapper _wrapper;
        public AlEventSubscriberAdapter(Type clrType, int codeunitId)
        {
            ApplicationObjectClrType = clrType;
            ApplicationObjectId = new ApplicationObjectId(ObjectType.CodeUnit, codeunitId);
            _wrapper = new NavEventSubscriberReflectionWrapper(clrType);
        }
        public bool OriginatesFromBase => false;
        public NavEventSubscriberReflectionWrapper SubscriberReflectionWrapper => _wrapper;
        public Type ApplicationObjectClrType { get; }
        public ApplicationObjectId ApplicationObjectId { get; }
        public bool IsEventManualBinding => false;
    }
}
