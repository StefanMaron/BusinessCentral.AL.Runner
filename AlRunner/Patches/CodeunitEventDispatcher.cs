// CodeunitEventDispatcher — runtime-layer replacement for BC's NavEventScope dispatch
// for codeunit-published IntegrationEvent / BusinessEvent.
//
// Wired via Cecil rewrite of NavMethodScope.OnRunEventAsync → call our dispatcher.
// Publishers reach that path because EventSubscriberPatches.SeedEventScopeSentinels()
// populates γeventScope with a structurally-valid sentinel NavEventScope on every
// publisher's <EventName>_Scope class.
//
// Per feedback_event_dispatch_must_be_universal.md, this is the ONLY architecture that
// covers events fired from any loaded DLL (MS BaseApp, SystemApp, ISV, our test bundles).
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;

namespace AlRunner;

public static partial class BcRuntime
{
    private static int _dispatchCount;
    private static int _dispatchFiredCount;

    internal const string PublisherKindCodeunit = "Codeunit";
    internal const string PublisherKindTable = "Record";
    // Page/Report/Query/XmlPort own-code (triggers, procedures, manually-declared events)
    // compiles to a class literally named "<Kind><N>" — unlike Table, there is no separate
    // metadata-only class to disambiguate from (issue #1794). Confirmed empirically by
    // reflecting over an emitted test assembly: a manually-declared [IntegrationEvent] on
    // each of these object kinds produces "Page90101+OnProbePageEvent_Scope",
    // "Report90102+OnProbeReportEvent_Scope", "Query90103+OnProbeQueryEvent_Scope",
    // "XmlPort90104+OnProbeXmlPortEvent_Scope" — each carrying the same
    // γeventScope : NavEventScope static field a codeunit publisher's scope class does.
    internal const string PublisherKindPage = "Page";
    internal const string PublisherKindReport = "Report";
    internal const string PublisherKindQuery = "Query";
    internal const string PublisherKindXmlPort = "XmlPort";

    // These six prefixes are mutually non-prefixing (none is a string-prefix of another),
    // so iteration order here is irrelevant — this is NOT sorted longest-first. The closest
    // near-miss is "Record" vs "Report", which merely SHARE a "Re" prefix; neither is a
    // prefix of the other, so it still doesn't matter. If a future object kind's prefix
    // IS a prefix of (or is prefixed by) one already in this list, order starts to matter
    // and this array must become longest-first — check that before adding a new entry.
    private static readonly string[] _publisherKindPrefixes =
    {
        PublisherKindCodeunit, PublisherKindTable,
        PublisherKindPage, PublisherKindReport, PublisherKindQuery, PublisherKindXmlPort,
    };

    /// <summary>
    /// Decode a publisher scope's declaring-type name into (kind, publisherId) —
    /// e.g. <c>"Codeunit50041"</c> → <c>(PublisherKindCodeunit, 50041)</c>,
    /// <c>"Record60976"</c> → <c>(PublisherKindTable, 60976)</c>,
    /// <c>"Page90101"</c> → <c>(PublisherKindPage, 90101)</c>. Any other prefix returns
    /// false — those publisher kinds are not registered by
    /// <c>EventSubscriberPatches.EnsureRegistryFresh</c>.
    ///
    /// Extracted as a pure, unit-testable seam (see DispatchObserveAsyncResultTests.cs for the
    /// established pattern of pinning dispatcher behavior at a seam that can't be reached from
    /// first-party AL). Issue #1770 was exactly a miss in this decode: only the "Codeunit"
    /// prefix was recognized, so a table object's OWN code — which compiles to a class named
    /// "Record&lt;N&gt;", not "Table&lt;N&gt;" — never matched, and a manually-declared
    /// [IntegrationEvent] raised from inside a table's trigger silently never dispatched.
    /// Issue #1794 extends the same decode to Page/Report/Query/XmlPort publishers, which had
    /// no branch here at all (not even a wrong one) — their manually-declared events were never
    /// recognized as a dispatchable publisher, so a subscriber to one silently never fired.
    /// </summary>
    internal static bool TryDecodeEventPublisherDeclType(string declTypeName, out string publisherKind, out int publisherId)
    {
        foreach (var prefix in _publisherKindPrefixes)
        {
            if (declTypeName.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(declTypeName.AsSpan(prefix.Length), out publisherId))
            {
                publisherKind = prefix;
                return true;
            }
        }
        publisherKind = "";
        publisherId = 0;
        return false;
    }

    /// <summary>
    /// Entry point called from the Cecil-rewritten NavMethodScope.OnRunEventAsync.
    /// Returns default ValueTask — synchronous execution model.
    /// </summary>
    private static bool _firstEntryLogged;

    // ── debug gates, read ONCE ────────────────────────────────────────────────────────
    // Both of these are developer switches, and this method runs on EVERY AL event scope.
    // Re-reading them per call put System.Environment.GetEnvironmentVariableCore in the
    // resolved leaf frame of 7 of 73 CPU samples of the single test in #2304. Memoised the
    // same way EventSubscriberPatches.ScanAuditEnabled already is; setting the variable
    // after the first dispatch has no effect, which is what ResetDispatchGatesForTests and
    // HotPathHookCostTests pin.
    private static bool? _dispatcherOff;
    private static bool? _dispatchTrace;

    internal static bool DispatcherDisabled
        => _dispatcherOff ??= Environment.GetEnvironmentVariable("AL_RUNNER_DISPATCHER_OFF") == "1";

    internal static bool DispatchTraceEnabled
        => _dispatchTrace ??= Environment.GetEnvironmentVariable("ALRUNNER_DISPATCH_TRACE") == "1";

    internal static void ResetDispatchGatesForTests()
    {
        _dispatcherOff = null;
        _dispatchTrace = null;
    }

    // DispatchCore used to resolve this per FIRED event with
    // AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "…Ncl"), which
    // materialises an AssemblyName (and with it a native GetCodeBase call) for every loaded
    // assembly until it hits the match. Resolved once instead; Ncl is loaded once per process
    // and never replaced. See LoadedRuntimeAssemblies.cs.
    private static Type? _tNavMethodScope;

    private static Type NavMethodScopeType
        => _tNavMethodScope ??= RequireRuntimeAssembly("Microsoft.Dynamics.Nav.Ncl")
            .GetType("Microsoft.Dynamics.Nav.Runtime.NavMethodScope")
           ?? throw new InvalidOperationException(
               "Microsoft.Dynamics.Nav.Runtime.NavMethodScope not found in Microsoft.Dynamics.Nav.Ncl");

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask CodeunitEventDispatch_OnRunEventAsync(object publisherScope)
    {
        if (!_firstEntryLogged) { _firstEntryLogged = true; Console.Error.WriteLine($"[Dispatch] entry-method first hit"); }
        if (DispatcherDisabled)
            return default;
        try { DispatchCore(publisherScope); }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            if (DispatchTraceEnabled)
                Console.Error.WriteLine(
                    $"[DispatchRethrow] {inner.GetType().Name}: {inner.Message}\nCALLER CHAIN:\n{Environment.StackTrace}");
            RethrowPreservingStack(inner);
        }
        return default;
    }

    /// <summary>
    /// Rethrow <paramref name="ex"/> preserving its ORIGINAL stack trace — the one place both
    /// dispatch rethrow sites (this entry point's catch above, and <see cref="DispatchCore"/>'s
    /// per-subscriber catch below) funnel through, so the fix can't regress independently in
    /// either. `throw ex;` (a bare rethrow of a caught-and-referenced exception) RESETS the
    /// exception's stack trace to the rethrow site, discarding every frame that identifies WHICH
    /// subscriber threw and where inside it — see #1955. Pinned directly by
    /// RethrowPreservingStackTests.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void RethrowPreservingStack(Exception ex)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        throw ex; // unreachable — DoesNotReturn tells the compiler .Throw() never returns, but a
                  // catch block invoking a void helper still needs a terminating statement of its
                  // own for callers that fall through it (e.g. DispatchCore's catch, below).
    }

    private static bool _firstDispatchLogged;
    private static bool _firstFireLogged;

    private static void DispatchCore(object publisherScope)
    {
        if (publisherScope == null) return;
        var scopeType = publisherScope.GetType();
        Interlocked.Increment(ref _dispatchCount);
        if (!_firstDispatchLogged) { _firstDispatchLogged = true; Console.Error.WriteLine($"[Dispatch] first call: scope={scopeType.FullName}"); }

        // Decode publisher id + event method name from scope type name.
        //   Microsoft.Dynamics.Nav.BusinessApplication.Codeunit50041+OnDoCalc_Scope
        //   Microsoft.Dynamics.Nav.BusinessApplication.Record50140+OnAfterXyz_Scope
        //
        // A manually-declared [IntegrationEvent]/[BusinessEvent] compiles to this same
        // generic <EventName>_Scope + OnRunEventAsync pattern regardless of which AL object
        // kind declares it — BC's NavTriggerEventType ordinals (Insert/Modify/Delete/Rename/
        // Validate) only cover the implicit table-trigger events, which fire through the
        // separate NavTableTriggerEventHandler path and never reach here. So a table object
        // that ALSO declares its own custom event needs the same universal dispatch as a
        // codeunit publisher, just keyed by table id instead of codeunit id (see issue #1770).
        // A table object's OWN code (triggers, procedures, and any manually-declared event)
        // compiles to a class named "Record<N>", NOT "Table<N>" (that name is a separate,
        // metadata-only class) — confirmed by reflecting over the emitted assembly.
        var declType = scopeType.DeclaringType;
        if (declType == null) return;
        var declName = declType.Name;
        var scopeName = scopeType.Name;
        int us = scopeName.IndexOf('_');
        if (us < 0) return;
        string eventMethodName = scopeName.Substring(0, us);

        if (!TryDecodeEventPublisherDeclType(declName, out var publisherKind, out int publisherId)) return;
        IReadOnlyList<MethodInfo>? subs = publisherKind switch
        {
            PublisherKindCodeunit => EventSubscriberPatches.GetCodeunitSubscribers(publisherId, eventMethodName),
            PublisherKindTable => EventSubscriberPatches.GetTableEventSubscribers(publisherId, eventMethodName),
            PublisherKindPage or PublisherKindReport or PublisherKindQuery or PublisherKindXmlPort
                => EventSubscriberPatches.GetObjectEventSubscribers(publisherKind, publisherId, eventMethodName),
            _ => null,
        };
        if (subs == null || subs.Count == 0) return;
        Interlocked.Increment(ref _dispatchFiredCount);
        if (!_firstFireLogged) { _firstFireLogged = true; Console.Error.WriteLine($"[Dispatch] first FIRE: {declName}.{eventMethodName} → {subs.Count} subs"); }
        if (DispatchTraceEnabled)
            Console.Error.WriteLine($"[Dispatch] FIRE {declName}.{eventMethodName} → {subs.Count} subs");

        // Publisher application object (NavCodeunit) — for NavCodeunitHandle.Target lookup of subscriber instances.
        var navMethodScopeType = NavMethodScopeType;
        var pubObj = navMethodScopeType.GetProperty("ApplicationObject", BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(publisherScope);
        if (pubObj == null) return;

        // Cross-assembly dedupe: in a multi-bundle run the SAME AL subscriber codeunit
        // can be emitted into two loaded assemblies (an impl bundle's own emit + the
        // dependent bundle's dep compile), and the registry scan collects the
        // MethodInfo from BOTH copies. That is ONE AL subscriber, so it must fire
        // exactly once — dispatch one per (codeunit id, method name, param shape);
        // InvokeOneSubscriber resolves the surviving MethodInfo against the
        // instance's actual runtime type, so which copy survives is irrelevant.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sub in subs)
        {
            if (!seen.Add(SubscriberAlIdentity(sub))) continue;
            try
            {
                if (IsManualBindingCodeunitType(sub.DeclaringType!))
                {
                    // EventSubscriberInstance = Manual: BC dispatches only to instances
                    // currently bound via BindSubscription — on those very instances, so
                    // the binder observes their state afterwards. Unbound → no fire at
                    // all (container-verified; the unbound default state must never leak
                    // into an event, e.g. System Application Test Library's 132513
                    // "Confirm Test Library" answering OnBeforeGuiAllowed with false).
                    foreach (var bound in BoundInstancesOf(ExtractCodeunitIdFromTypeName(sub.DeclaringType!)))
                        InvokeOneSubscriber(publisherScope, scopeType, pubObj, sub, bound);
                }
                else
                {
                    InvokeOneSubscriber(publisherScope, scopeType, pubObj, sub, null);
                }
            }
            catch (TargetInvocationException tie)
            {
                if (DispatchTraceEnabled)
                    Console.Error.WriteLine(
                        $"[DispatchThrow] {declName}.{eventMethodName} subscriber threw: "
                        + $"{(tie.InnerException ?? tie).GetType().Name}: {(tie.InnerException ?? tie).Message}");
                RethrowPreservingStack(tie.InnerException ?? tie);
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _manualBindingTypeCache = new();

    /// <summary>Drop the manual-binding type cache on a server-mode bundle reload — its keys
    /// are Types from the previous bundle's emitted assembly (same reason
    /// EventSubscriberPatches.ResetForReload clears its codeunit-type cache).</summary>
    internal static void ResetManualBindingCacheForReload() => _manualBindingTypeCache.Clear();

    /// <summary>
    /// Does this AL-emitted Codeunit{N} class declare <c>EventSubscriberInstance = Manual</c>?
    /// Read off [NavCodeunitOptionsAttribute] exactly like
    /// <see cref="NCLMetaCodeunit_get_IsEventManualBinding"/> (which serves BC's own
    /// BindSubscription path) so both sides agree on what "manual" is.
    ///
    /// The single definition of "Manual" in the runner: the codeunit-event dispatcher below
    /// consults it directly, and the table-event path reports it out of
    /// <c>EventSubscriberPatches.AlEventSubscriberAdapter.IsEventManualBinding</c> so BC's own
    /// NavEventScope dispatch classifies the subscription the same way. Two answers here would
    /// let the two paths drift on what Manual means.
    /// </summary>
    internal static bool IsManualBindingCodeunitType(Type codeunitClrType)
        => _manualBindingTypeCache.GetOrAdd(codeunitClrType, static t =>
        {
            foreach (var attr in t.GetCustomAttributes(inherit: false))
            {
                var at = attr.GetType();
                if (at.Name != "NavCodeunitOptionsAttribute") continue;
                var isManual = at.GetProperty("IsEventManualBinding",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (isManual != null)
                {
                    try { return (bool)isManual.GetValue(attr)!; } catch { }
                }
                var optionsProp = at.GetProperty("Options",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var v = optionsProp?.GetValue(attr);
                if (v != null)
                    return (Convert.ToInt32(v) & 1) != 0; // EventManualBinding flag = 1
            }
            return false;
        });

    private static PropertyInfo? _piSessionEventBindings;
    private static bool _eventBindingsLookupFailed;

    /// <summary>
    /// Instances of codeunit <paramref name="codeunitId"/> currently bound via AL
    /// BindSubscription. BC's own NavCodeunit.BindSubscription/UnbindSubscription bodies
    /// maintain <c>Session.EventBindings</c> (a List&lt;NavCodeunit&gt; seeded on the
    /// skeleton session); reading that list keeps bind/unbind/double-bind semantics BC's
    /// business, not ours. Snapshot before invoking — a subscriber body may bind/unbind.
    /// </summary>
    private static List<object> BoundInstancesOf(int codeunitId)
    {
        var result = new List<object>();
        if (codeunitId == 0) return result;
        var session = SkeletonSession;
        if (session == null) return result;
        if (_piSessionEventBindings == null && !_eventBindingsLookupFailed)
        {
            _piSessionEventBindings = session.GetType().GetProperty("EventBindings",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_piSessionEventBindings == null)
            {
                _eventBindingsLookupFailed = true;
                Console.Error.WriteLine(
                    "[Dispatch] NavSession.EventBindings NOT FOUND — Ncl shape changed; "
                    + "manually-bound event subscribers will never fire");
            }
        }
        if (_piSessionEventBindings?.GetValue(session) is not System.Collections.IEnumerable bindings)
            return result;
        foreach (var bound in bindings)
        {
            if (bound != null && ExtractCodeunitIdFromTypeName(bound.GetType()) == codeunitId)
                result.Add(bound);
        }
        return result;
    }

    /// <summary>
    /// <paramref name="publisher"/> when its tree is still usable, otherwise the skeleton
    /// session. Only the tree-parent role is being substituted — the publisher itself is
    /// still what the subscriber is dispatched for.
    /// </summary>
    private static object LiveParentFor(object publisher)
    {
        try
        {
            var tree = publisher.GetType()
                .GetProperty("Tree", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(publisher);
            if (tree != null) return publisher;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is ObjectDisposedException) { }
        catch (ObjectDisposedException) { }

        return AlRunner.BcRuntime.SkeletonSession ?? publisher;
    }

    /// <summary>
    /// Assembly-independent identity of an AL subscriber method: declaring codeunit
    /// id + method name + parameter type NAMES (full CLR identity would differ per
    /// emitted assembly for the same AL code).
    /// </summary>
    private static string SubscriberAlIdentity(MethodInfo m)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(ExtractCodeunitIdFromTypeName(m.DeclaringType!)).Append(':').Append(m.Name);
        foreach (var p in m.GetParameters())
            sb.Append('|').Append(p.ParameterType.Name);
        return sb.ToString();
    }

    /// <summary>
    /// Is this parameter (positionally) capable of being the "Sender" param of an
    /// IncludeSender=true event subscriber? AL emits it as a positional first parameter whose
    /// name is "Sender" (case-insensitive) and whose type is either <c>NavCodeunitHandle</c> (or
    /// a typed codeunit-handle subclass, for a codeunit publisher) or <c>INavRecordHandle</c> (an
    /// interface, or a concrete <c>Record&lt;N&gt;</c> implementing it, for a TABLE publisher —
    /// #1956). This only answers the TYPE-SHAPE question; the call site additionally requires
    /// no scope field matched this parameter's name before treating it as sender (see
    /// InvokeOneSubscriber) — that's what distinguishes an actual sender from a coincidentally
    /// leading, genuinely-declared record/codeunit-typed event argument.
    /// </summary>
    internal static bool IsSenderParameter(ParameterInfo p, int paramIndex)
    {
        if (paramIndex != 0) return false;
        // Codeunit publisher: AL emits sender as Codeunit50047 (the publisher CLR type) — the
        // bundle's typed handle. The runtime type ancestry traces back to NavCodeunitHandle.
        var t = p.ParameterType;
        while (t != null && t != typeof(object))
        {
            if (t.Name == "NavCodeunitHandle" || t.Name.StartsWith("Codeunit", StringComparison.Ordinal)) return true;
            t = t.BaseType;
        }
        // Table publisher: AL emits sender as INavRecordHandle, an INTERFACE — the CLR BaseType
        // walk above can never reach an interface, so it needs its own assignability check.
        if (typeof(Microsoft.Dynamics.Nav.Runtime.INavRecordHandle).IsAssignableFrom(p.ParameterType)) return true;
        return false;
    }

    private static Type? _tNavCodeunitHandle;
    private static ConstructorInfo? _ciNavCodeunitHandleByIdInt;
    private static ConstructorInfo? _ciNavCodeunitHandleByInstance;
    private static PropertyInfo? _pNavCodeunitHandle_Target;

    private static void InvokeOneSubscriber(object publisherScope, Type scopeType, object treeObj, MethodInfo subscriberMethod,
        object? boundInstance)
    {
        EnsureCodeunitHandleReflection();

        var subscriberClrType = subscriberMethod.DeclaringType!;
        int subscriberCodeunitId = ExtractCodeunitIdFromTypeName(subscriberClrType);
        if (subscriberCodeunitId == 0) return;

        object? subscriberInstance = boundInstance;
        if (subscriberInstance == null)
        {
            // The subscriber handle is parented on the PUBLISHER, and a publisher can outlive the
            // scope it was created in — a SingleInstance codeunit is cached for the whole test, so
            // by the time it publishes again its original tree may be disposed. Everything that
            // reads the tree off it then throws ObjectDisposedException("Tree"), which took out
            // event dispatch for the rest of the run (measured on Base App codeunit 43 publishing
            // OnGetLanguageIdOrDefault). Parenting on the session instead is what BC's own
            // "resolve this codeunit in the current session" amounts to, and the session is alive
            // for exactly as long as dispatch can be running.
            var handle = _ciNavCodeunitHandleByIdInt!.Invoke(
                new object?[] { LiveParentFor(treeObj), subscriberCodeunitId });
            subscriberInstance = _pNavCodeunitHandle_Target!.GetValue(handle);
        }
        if (subscriberInstance == null) return;

        // Cross-assembly type mismatch: the registry may hold this MethodInfo from a
        // DIFFERENT emitted assembly than the one the codeunit registry instantiated
        // (same AL codeunit compiled into both an impl bundle's own emit and the
        // dependent bundle's dep assembly). MethodInfo.Invoke would then throw
        // TargetException 'Object does not match target type' — re-resolve the handler
        // against the instance's ACTUAL runtime type (same AL body, canonical copy).
        if (!subscriberClrType.IsInstanceOfType(subscriberInstance))
        {
            var remapped = ResolveOnInstanceType(subscriberInstance.GetType(), subscriberMethod);
            if (remapped == null)
                throw new InvalidOperationException(
                    $"[Dispatch] subscriber {subscriberClrType.FullName}.{subscriberMethod.Name} " +
                    $"({subscriberClrType.Assembly.GetName().Name}) has no matching method on the " +
                    $"instantiated type {subscriberInstance.GetType().FullName} " +
                    $"({subscriberInstance.GetType().Assembly.GetName().Name}) — cross-assembly " +
                    "codeunit copies diverged; refusing to silently skip the subscriber.");
            subscriberMethod = remapped;
        }

        // Match subscriber parameters by name to publisher-scope instance fields.
        // Case-insensitive — AL emit lowercases C# fields, but ParameterInfo.Name preserves AL casing.
        // Special case: leading "Sender" parameter on IncludeSender=true subscriber receives the
        // publisher itself (wrapped in a NavCodeunitHandle for a codeunit publisher, or passed
        // straight through for a table publisher — see IsSenderParameter/#1956).
        //
        // Field lookup runs FIRST and wins: an IncludeSender=true event declares no parameters
        // at all, so AL never emits a scope field for its "Sender" — the field-vs-sender
        // branches can never both match the SAME parameter. A leading parameter that DOES have
        // a matching scope field is a genuinely declared, record-typed (or codeunit-typed)
        // event ARGUMENT, not a sender, and must keep taking its value from the scope.
        int publisherCodeunitId = ExtractCodeunitIdFromTypeName(treeObj.GetType());
        var parms = subscriberMethod.GetParameters();
        var args = new object?[parms.Length];
        for (int i = 0; i < parms.Length; i++)
        {
            var p = parms[i];
            var fld = scopeType.GetField(p.Name!,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fld == null && IsSenderParameter(p, i))
            {
                if (typeof(Microsoft.Dynamics.Nav.Runtime.INavRecordHandle).IsAssignableFrom(p.ParameterType))
                {
                    // Table publisher: AL emits the sender parameter as INavRecordHandle (or a
                    // concrete Record<N> implementing it). `treeObj` (NavMethodScope's
                    // ApplicationObject) IS that record instance already — pass it straight
                    // through instead of wrapping it in a codeunit handle (#1956).
                    args[i] = CoerceArg(treeObj, p.ParameterType);
                }
                else
                {
                    // Codeunit publisher: use the (ITreeObject, NavCodeunit) ctor to wrap the
                    // EXISTING publisher instance — the (ITreeObject, int) ctor would create a
                    // fresh instance via the codeunit registry, losing any publisher-side state
                    // the subscriber needs.
                    args[i] = _ciNavCodeunitHandleByInstance != null
                        ? _ciNavCodeunitHandleByInstance.Invoke(new object?[] { treeObj, treeObj })
                        : _ciNavCodeunitHandleByIdInt!.Invoke(new object?[] { treeObj, publisherCodeunitId });
                }
                continue;
            }
            args[i] = CoerceArg(fld?.GetValue(publisherScope), p.ParameterType);
            if (DispatchTraceEnabled
                && subscriberMethod.Name.Contains("CustomDocumentMerger", StringComparison.OrdinalIgnoreCase))
            {
                string repr;
                try { repr = args[i]?.ToString() ?? "null"; } catch { repr = "<unreadable>"; }
                if (repr.Length > 160) repr = repr.Substring(0, 160) + "…";
                Console.Error.WriteLine(
                    $"[DispatchArg] {subscriberMethod.DeclaringType!.Name}.{subscriberMethod.Name} "
                    + $"param '{p.Name}' ({p.ParameterType.Name}) field={(fld == null ? "MISSING" : fld.Name)} value={repr.Replace("\n", " ")}");
            }
        }
        ObserveAsyncResult(subscriberMethod.Invoke(subscriberInstance, args), subscriberMethod);
    }

    /// <summary>
    /// Observe an AL subscriber's return value when it is a Task/ValueTask.
    ///
    /// AL subscribers are NOT always emitted as synchronous methods: BC emits an async
    /// state machine whenever the body needs one (the System App's
    /// ReportManagement.OnCustomDocumentMergerEx is one). An exception raised inside such a
    /// body is CAPTURED BY THE STATE MACHINE and surfaced on the returned task — it does not
    /// propagate out of MethodInfo.Invoke. Discarding that task therefore discarded the
    /// error: the publisher continued as if the subscriber had succeeded, and the AL author
    /// saw a silent wrong answer instead of their own Error().
    ///
    /// Measured: an ISV report renderer raised
    /// "LF-XML: The template is not well-formed XML" from inside the custom document merger;
    /// the platform went on to hand the caller an EMPTY document and Report.SaveAs reported
    /// success, so the test failed later with a misleading "Not a native PDF" instead of the
    /// real diagnostic.
    ///
    /// Blocking here is correct for this runtime: the runner drives AL synchronously (see
    /// NavReportSync and the OnRunEventAsync rewrite), so these tasks are already complete
    /// or complete inline. GetAwaiter().GetResult() also rethrows the ORIGINAL exception
    /// rather than an AggregateException, which is what AL callers must observe.
    /// </summary>
    public static void ObserveAsyncResult(object? result, MethodInfo subscriberMethod)
    {
        switch (result)
        {
            case null:
                return;
            case Task t:
                t.GetAwaiter().GetResult();
                return;
            case ValueTask vt:
                vt.GetAwaiter().GetResult();
                return;
        }

        var ty = result.GetType();
        if (ty.IsGenericType && ty.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = ty.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    $"[Dispatch] ValueTask<T>.AsTask not found while observing {subscriberMethod.Name} — BC/runtime shape changed.");
            ((Task)asTask.Invoke(result, null)!).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Find the method on <paramref name="instanceType"/> matching a registry
    /// MethodInfo that was scanned from a different emitted assembly's copy of the
    /// same AL codeunit: same name, same arity, same parameter type NAMES (the CLR
    /// types themselves differ per assembly for e.g. ByRef&lt;T&gt; of emitted types).
    /// </summary>
    private static MethodInfo? ResolveOnInstanceType(Type instanceType, MethodInfo template)
    {
        var tps = template.GetParameters();
        MethodInfo? match = null;
        foreach (var m in instanceType.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            if (m.Name != template.Name) continue;
            var ps = m.GetParameters();
            if (ps.Length != tps.Length) continue;
            bool ok = true;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].ParameterType.Name != tps[i].ParameterType.Name) { ok = false; break; }
            if (!ok) continue;
            match = m;
            break;
        }
        return match;
    }

    private static Type? _tNavOption;
    private static PropertyInfo? _pNavOption_Value;

    /// <summary>
    /// Coerce a publisher event-scope field value to the subscriber parameter's CLR type.
    ///
    /// AL compiles an <c>Option</c>-typed event argument so that the publisher's scope
    /// field carries a runtime <c>NavOption</c> (the option carrier, holding the integer
    /// ordinal in <c>.Value</c>), while a subscriber declaring the same parameter as an
    /// AL <c>Option</c> emits it as a plain <c>Int32</c> slot. Reflection's
    /// <c>MethodInfo.Invoke</c> does no NavOption→Int32 conversion, so the raw value must
    /// be unwrapped to its ordinal here. The mirror case (subscriber declares the param as
    /// the NavOption carrier, field is an int) is handled too. This is faithful: a NavOption
    /// is exactly its integer ordinal as far as an AL Option/Integer parameter observes.
    ///
    /// A precompiled application DLL additionally stores a scope field
    /// <c>ByRef&lt;T&gt;</c>-wrapped when the publishing method captured the value slot by
    /// reference before raising the event (issue #1816: Base App
    /// <c>Item.CheckDocuments</c>'s <c>currentFieldNo</c>), while the subscriber's
    /// parameter is by value. The wrapper is a getter/setter pair over the slot, so the
    /// faithful by-value observation is the slot's current value at dispatch time —
    /// unwrap it and re-coerce (the inner value may itself be a NavOption). When the
    /// subscriber's parameter IS the ByRef&lt;T&gt; (AL <c>var</c>), the assignability
    /// early-return above already passed the wrapper through untouched, keeping writeback.
    ///
    /// All other types pass through unchanged.
    /// </summary>
    internal static object? CoerceArg(object? value, Type paramType)
    {
        if (value == null) return null;
        var vt = value.GetType();
        if (paramType.IsAssignableFrom(vt)) return value;

        if (vt.IsGenericType
            && vt.GetGenericTypeDefinition() == typeof(Microsoft.Dynamics.Nav.Runtime.ByRef<>))
        {
            object? inner = vt
                .GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(value);
            return CoerceArg(inner, paramType);
        }

        EnsureNavOptionReflection();

        // NavOption field → Int32 (or other integral) subscriber parameter: unwrap ordinal.
        if (_tNavOption != null && _tNavOption.IsAssignableFrom(vt))
        {
            object? ordinal = _pNavOption_Value?.GetValue(value);
            if (ordinal != null)
            {
                var underlying = paramType.IsEnum ? Enum.GetUnderlyingType(paramType) : paramType;
                if (underlying == typeof(int) || underlying == typeof(long)
                    || underlying == typeof(short) || underlying == typeof(byte))
                {
                    object converted = Convert.ChangeType(ordinal, underlying);
                    return paramType.IsEnum ? Enum.ToObject(paramType, converted) : converted;
                }
            }
        }

        return value;
    }

    private static void EnsureNavOptionReflection()
    {
        if (_tNavOption != null) return;
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        _tNavOption = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavOption");
        _pNavOption_Value = _tNavOption?.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
    }

    private static int ExtractCodeunitIdFromTypeName(Type t)
    {
        var n = t.Name;
        if (!n.StartsWith("Codeunit", StringComparison.Ordinal)) return 0;
        return int.TryParse(n.AsSpan("Codeunit".Length), out int id) ? id : 0;
    }

    private static void EnsureCodeunitHandleReflection()
    {
        if (_tNavCodeunitHandle != null) return;
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        _tNavCodeunitHandle = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle")!;
        var navCodeunitType = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavCodeunit")!;
        foreach (var c in _tNavCodeunitHandle.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var ps = c.GetParameters();
            if (ps.Length != 2) continue;
            if (ps[1].ParameterType == typeof(int)) _ciNavCodeunitHandleByIdInt = c;
            else if (ps[1].ParameterType == navCodeunitType) _ciNavCodeunitHandleByInstance = c;
        }
        var t = _tNavCodeunitHandle;
        while (t != null)
        {
            var p = t.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
            if (p != null) { _pNavCodeunitHandle_Target = p; break; }
            t = t.BaseType;
        }
    }

    public static int CodeunitEventDispatchCount => _dispatchCount;
    public static int CodeunitEventFiredCount => _dispatchFiredCount;
}
