// TruncateValidationPatches — a faithful stand-in for NavRecord.ValidateTruncateSupport.
//
// BC's ValidateTruncateSupport is the whole of Record.Truncate()'s precondition check. It runs
// seven guards in order, each raising its own distinct message, and the runner used to replace
// the method with NoOp_OneArg — so every one of them was discarded and Truncate() succeeded
// where real BC refuses. The comment on that registration explains why it was a no-op: guard 4
// reads recordImplementation.RequiresSecurityFiltersValidation, which answers true on the
// skeleton and raised NavPermissionException for every caller. Dropping ALL seven to silence
// ONE is the silent-fake shape .claude/rules/loud-failures.md exists to stop.
//
// This file keeps the six guards that are faithful on the skeleton and skips only guard 4.
// See docs/limitations.md#truncate-security-filter-validation.
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner;

public static partial class BcRuntime
{
    // Resolved once, from the Ncl assembly the record type itself came from, so a BC version
    // change cannot silently bind these against a stale assembly.
    private static bool _truncateValidationResolved;
    private static PropertyInfo? _pRecIsTemporary;       // NavRecord.IsTemporary
    private static PropertyInfo? _pRecMetaTable;         // NavRecord.MetaTable
    private static PropertyInfo? _pMtSupportsTruncation; // NCLMetaTable.SupportsTruncation
    private static PropertyInfo? _pMtMediaFieldCount;    // NCLMetaTable.MediaFieldCount
    private static Type? _navCSideTruncateExceptionType;

    // Guard 5 — the delete-event subscription check.
    private static PropertyInfo? _pRecSession;           // NavRecord.Session
    private static MethodInfo? _mResolveAppGroup;        // NavCurrentThread.ResolveAppGroup(NavSession)
    private static MethodInfo? _mMtIsEventSubscribed;    // NCLMetaTable.IsEventSubscribed(NavTriggerEventType, NavAppGroup)
    private static Type? _navTriggerEventType;           // read off IsEventSubscribed's own signature

    // Guard 7 — marked records / FlowField filters.
    private static PropertyInfo? _pRecRecordImplementation; // NavRecord.RecordImplementation
    private static PropertyInfo? _pImplTableState;          // RecordImplementation.TableState
    private static PropertyInfo? _pTsFiltersAndMarks;       // TableState.FiltersAndMarks
    private static PropertyInfo? _pFamMarkedRecords;        // FiltersAndMarks.MarkedRecords
    private static PropertyInfo? _pFamFilters;              // FiltersAndMarks.Filters
    private static PropertyInfo? _pMrIsCompleteExpressionLarge;
    private static PropertyInfo? _pFfdAnyFiltersOnFlowFields;

    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Replacement for NavRecord.ValidateTruncateSupport(NavRecord).
    ///
    /// <para>Observably equivalent to BC for in-scope test code on six of its seven guards: each
    /// one is re-raised as the same NavCSideTruncateException carrying the same Lang message, so
    /// AL sees the text real BC produces. Guard 4 (RequiresSecurityFiltersValidation →
    /// NavPermissionException) is deliberately skipped: the skeleton has no security-filter state
    /// for it to read and it answers true unconditionally, which refused every Truncate() —
    /// docs/limitations.md#truncate-security-filter-validation.</para>
    ///
    /// <para>Citation: Ncl 28.1.49838.53910 (sha256 49b11d9b), NavRecord.ValidateTruncateSupport;
    /// the guard order and messages are its own. Byte-identical body on 27.0 and 28.4
    /// (compare_symbols, bodyChanged=false). Corpus codeunit 60923 pins the try-scope guard.</para>
    ///
    /// <para>Trap: the ORDER is load-bearing, because several guards can hold at once and BC
    /// reports the first. A test that seeds a temporary table and expects the try-function
    /// message measures guard 1, not guard 3.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecord_ValidateTruncateSupport(object? record)
    {
        if (record == null) return;
        EnsureTruncateValidationShape(record.GetType());

        // Guard 1 — IsTemporary.
        if (_pRecIsTemporary?.GetValue(record) is true)
            ThrowTruncate("Temporary tables does not support truncation.");

        var metaTable = _pRecMetaTable?.GetValue(record);

        // Guard 2 — MetaTable.SupportsTruncation. Only asserted when the property resolved and
        // answered false; a null read is "could not measure", which must not become a refusal.
        //
        // Unlike guards 1, 3 and 6 this one has NO corpus arm, and not for want of trying:
        // SupportsTruncation is `TableType == Normal && (!IsSystemTable || TableId == 2000000295)`,
        // so reaching it needs a non-Normal table. The corpus has two — "ALT Temp Only"
        // (TableType = Temporary), which guard 1 catches first, and "ALT CRM Entity" (60291,
        // TableType = CRM), which throws "Table connection for table type CRM must be registered
        // ..." before ValidateTruncateSupport runs at all (measured). So the message here is
        // pinned only by the resource string it was read from, not by a service tier.
        if (metaTable != null && _pMtSupportsTruncation?.GetValue(metaTable) is false)
            ThrowTruncate("The table does not support truncation.");

        // Guard 3 — the try scope. This is the one #4371 is about: BC reads
        // Session.CurrentMethodScope.IsInTryScope, which EnterTryScope sets for the body of an
        // AL [TryFunction] and NavMethodScopeCtorReplacement inherits into the frames below it.
        if (CurrentScopeIsInTryScope())
            ThrowTruncate("Truncate is not supported in try functions.");

        // Guard 4 — RequiresSecurityFiltersValidation. SKIPPED; see the summary above.

        // Guard 5 — OnBeforeDelete/OnAfterDelete subscribers.
        if (metaTable != null && IsDeleteEventSubscribed(record, metaTable))
            ThrowTruncate("Truncate is not supported when the OnBeforeDelete and/or OnAfterDelete "
                + "event is subscribed. Please remove the events subscriptions or use DeleteAll.");

        // Guard 6 — MediaFieldCount.
        if (metaTable != null && _pMtMediaFieldCount?.GetValue(metaTable) is int mediaFields && mediaFields > 0)
            ThrowTruncate("Truncate is not supported when the field has Media and/or MediaSet fields.");

        // Guard 7 — marked records, then FlowField filters. Two separate BC guards reading one
        // FiltersAndMarks, in this order.
        var filtersAndMarks = ReadFiltersAndMarks(record);
        if (filtersAndMarks != null)
        {
            var marked = _pFamMarkedRecords!.GetValue(filtersAndMarks);
            if (marked != null && _pMrIsCompleteExpressionLarge!.GetValue(marked) is true)
                ThrowTruncate("Too many marks to truncate, use DeleteAll or use different filtering.");

            // BC: FlowFieldsHelper.AnyFiltersOnFlowFields(f) => f.Filters?.AnyFiltersOnFlowFields
            // ?? false — a null Filters is "no filters", which is a legitimate false, not a gap.
            var filters = _pFamFilters!.GetValue(filtersAndMarks);
            if (filters != null && _pFfdAnyFiltersOnFlowFields!.GetValue(filters) is true)
                ThrowTruncate("Truncate does not support filters on FlowFields.");
        }
    }

    /// <summary>
    /// BC's guard 5: <c>NCLMetaTable.IsEventSubscribed(OnBeforeDeleteEvent | OnAfterDeleteEvent,
    /// NavCurrentThread.ResolveAppGroup(record.Session))</c>.
    ///
    /// <para>Faithful on the skeleton because both sides of the app-group comparison are the
    /// runner's own, and they agree: <c>ResolveAppGroup</c> reads
    /// <c>session.OverriddenAppGroup ?? session.NavAppGroup</c>, and BcRuntime sets
    /// <c>OverriddenAppGroup = NavAppGroup.BaseGroup</c> on the skeleton session, while
    /// <c>EventSubscriberPatches.BuildSubscription</c> stamps that same <c>BaseGroup</c> on every
    /// subscription it registers. BC narrows by <c>SubscriberNavAppGroup.GroupId</c>
    /// (<c>NavEventScope.GetAppGroupSubscriberStartIndex</c>), so one group id on both sides is
    /// the whole of what the comparison needs. Measured in-process on this fixture set:
    /// <c>groupId=0</c> either side, <c>onBeforeDelete=True</c> for the subscribed table and
    /// <c>False</c> for three unsubscribed ones.</para>
    ///
    /// <para>Citation: Ncl 28.1.49838.53910 (sha256 49b11d9b) —
    /// <c>NavRecord.ValidateTruncateSupport</c>, <c>NavCurrentThread.TryResolveAppGroup</c>,
    /// <c>NavEventScope.HasSubscribersForAppGroup</c>. The registry this reads is BC's own and is
    /// already load-bearing in the runner: <c>NavRecord.InsertAsync</c> calls the trigger handler
    /// only once <c>IsEventSubscribed</c> says yes, and #3576 removed a constant-true rewrite of
    /// it (<c>IsEventSubscribedNotConstantTests</c>). Corpus codeunit 60518 adjudicates the
    /// AL-observable claim on a real service tier.</para>
    ///
    /// <para>Trap: this answers about the app group the runner actually runs in. A future change
    /// that gives published apps distinct group ids must keep the subscription's stamped group and
    /// the session's resolved group in step, or this silently stops matching.</para>
    /// </summary>
    private static bool IsDeleteEventSubscribed(object record, object metaTable)
    {
        // Every field this reads is bound by ResolveGuard5Shape, which REFUSES rather than
        // returning when any of them is null — so reaching this method with one null is
        // impossible by construction. It is re-asserted rather than assumed because the failure
        // it would otherwise produce is silent: a `return false` here permits a Truncate() BC
        // refuses, and nothing anywhere says the guard stopped measuring
        // (.claude/rules/guards-need-a-third-state.md — a reflection bind answering null is
        // unmeasurable, not absent).
        if (_mMtIsEventSubscribed == null || _navTriggerEventType == null
            || _mResolveAppGroup == null || _pRecSession == null)
            throw new AlRunner.Infrastructure.BcShapeGapException(
                "Record.Truncate()",
                "NCLMetaTable.IsEventSubscribed / NavCurrentThread.ResolveAppGroup",
                "the delete-subscriber guard was reached with an unbound member "
                + $"(Session={_pRecSession != null}, IsEventSubscribed={_mMtIsEventSubscribed != null}, "
                + $"TriggerEventType={_navTriggerEventType != null}, "
                + $"ResolveAppGroup={_mResolveAppGroup != null})");

        var session = _pRecSession.GetValue(record);
        var appGroup = _mResolveAppGroup.Invoke(null, new[] { session });
        if (appGroup == null)
            throw new AlRunner.Infrastructure.BcShapeGapException(
                "Record.Truncate()", "NavCurrentThread.ResolveAppGroup",
                "returned null; BC's own body falls back to NavAppGroup.BaseGroup and never does");

        // Ordinals 5 and 6 are OnBeforeDeleteEvent / OnAfterDeleteEvent, the same pair
        // EventSubscriberPatches.ResolveEventOrdinalFromName registers under.
        foreach (var ordinal in new[] { 5, 6 })
        {
            var subscribed = _mMtIsEventSubscribed.Invoke(
                metaTable, new[] { Enum.ToObject(_navTriggerEventType, ordinal), appGroup });
            if (subscribed is true) return true;
        }
        return false;
    }

    /// <summary>
    /// <c>record.RecordImplementation.TableState.FiltersAndMarks</c>, or null when the record has
    /// no table state to read — which is an ordinary state for a record that has never been
    /// opened, not a gap. A failure to BIND any step is a gap and refuses in
    /// <see cref="EnsureTruncateValidationShape"/> instead.
    /// </summary>
    private static object? ReadFiltersAndMarks(object record)
    {
        if (_pRecRecordImplementation == null || _pImplTableState == null || _pTsFiltersAndMarks == null)
            return null;
        var impl = _pRecRecordImplementation.GetValue(record);
        if (impl == null) return null;
        var tableState = _pImplTableState.GetValue(impl);
        if (tableState == null) return null;
        return _pTsFiltersAndMarks.GetValue(tableState);
    }

    /// <summary>
    /// Raises BC's own NavCSideTruncateException, so `asserterror` and TryFunction classify it
    /// exactly as they do BC's throw: it derives from NavCSideException, which NavRecord's
    /// `catch (NavCSideException) when (dataError == DataError.TrapError)` traps for the boolean
    /// `if Rec.Truncate() then` form. A plain Exception here would tear through that catch.
    /// </summary>
    [DoesNotReturn]
    private static void ThrowTruncate(string message)
    {
        // The exception is constructed HERE and thrown on the same expression, never assigned to
        // a local and re-thrown. `throw someLocal;` resets the stack trace to the throw site,
        // which is the #1955 / #2925 / #2948 defect RethrowPreservesOriginFrameTests ratchets
        // against — and it would be a poor thing to reintroduce in the patch that exists to make
        // AL call stacks read faithfully. A brand-new exception takes `throw new`/`throw <ctor>`;
        // ExceptionDispatchInfo is for a caught exception that already has frames to preserve.
        if (_navCSideTruncateExceptionType != null)
            throw (Exception)Activator.CreateInstance(_navCSideTruncateExceptionType, message)!;

        // Could not bind BC's exception type: refuse loudly rather than let the Truncate proceed,
        // which would be the silent-fake this file exists to remove.
        throw new AlRunner.Infrastructure.BcShapeGapException(
            "Record.Truncate()",
            "Microsoft.Dynamics.Nav.Types.Exceptions.NavCSideTruncateException",
            $"could not construct BC's truncate exception to report: {message}");
    }

    private static bool CurrentScopeIsInTryScope()
    {
        if (_fMsFlags == null || _fSessCurrentScope == null || _skeletonSession == null) return false;
        var scope = _fSessCurrentScope.GetValue(_skeletonSession);
        if (scope == null) return false;
        var flags = _fMsFlags.GetValue(scope);
        if (flags == null) return false;
        var isInTryScope = Convert.ToInt64(Enum.Parse(_fMsFlags.FieldType, "IsInTryScope"));
        return (Convert.ToInt64(flags) & isInTryScope) != 0;
    }

    private static void EnsureTruncateValidationShape(Type recordType)
    {
        if (_truncateValidationResolved) return;
        _truncateValidationResolved = true;

        _pRecIsTemporary = FindPropertyUpHierarchy(recordType, "IsTemporary");
        _pRecMetaTable = FindPropertyUpHierarchy(recordType, "MetaTable");

        // NOT recordType.Assembly: for an AL-emitted record that is the emitted BUSINESS
        // APPLICATION assembly, which has no BC types in it, so NCLMetaTable resolved to null and
        // guards 2 and 6 were silently inert for every AL table (measured on this fixture set —
        // `metaTableType=NULL`). Take the type from the MetaTable property's own declared type,
        // which is a BC type whatever assembly the record came from, and fall back to a scan.
        var metaTableType = _pRecMetaTable?.PropertyType
            ?? recordType.Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable"))
                .FirstOrDefault(t => t != null);
        _pMtSupportsTruncation = metaTableType?.GetProperty("SupportsTruncation");
        _pMtMediaFieldCount = metaTableType?.GetProperty("MediaFieldCount");

        // The exception lives in Types.dll, not Ncl.dll, so it is resolved by name across the
        // loaded set rather than off `ncl`.
        _navCSideTruncateExceptionType =
            Type.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavCSideTruncateException, Microsoft.Dynamics.Nav.Types")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavCSideTruncateException"))
                .FirstOrDefault(t => t != null);

        ResolveGuard5Shape(recordType, metaTableType);
        ResolveGuard7Shape(recordType);
    }

    /// <summary>
    /// Binds guard 5. Every member here is REQUIRED: a null bind means "BC moved and we cannot
    /// tell whether a delete subscriber exists", which is not the same as "there is no
    /// subscriber" — and resolving it toward the latter would silently permit a Truncate() BC
    /// refuses, the exact silent-fake this file exists to remove
    /// (.claude/rules/guards-need-a-third-state.md).
    /// </summary>
    private static void ResolveGuard5Shape(Type recordType, Type? metaTableType)
    {
        _pRecSession = FindPropertyUpHierarchy(recordType, "Session");

        // NavTriggerEventType lives in Types.dll while NCLMetaTable lives in Ncl.dll, so the
        // enum is read off IsEventSubscribed's OWN signature rather than resolved by name —
        // a namespace guess binds nothing and would read as "no overload".
        //
        // Walked up the hierarchy because IsEventSubscribed is declared on the BASE type
        // NCLMetaApplicationObject, not on NCLMetaTable: a plain GetMethods on the derived type
        // binds nothing, which is indistinguishable from "BC removed it" (measured — the first
        // run of this guard refused with IsEventSubscribed=False for exactly this reason).
        for (var t = metaTableType; t != null && _mMtIsEventSubscribed == null; t = t.BaseType)
            _mMtIsEventSubscribed = t.GetMethods(AnyInstance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == "IsEventSubscribed"
                                     && m.ReturnType == typeof(bool)
                                     && m.GetParameters().Length == 2
                                     && m.GetParameters()[0].ParameterType.IsEnum);
        _navTriggerEventType = _mMtIsEventSubscribed?.GetParameters()[0].ParameterType;
        var appGroupType = _mMtIsEventSubscribed?.GetParameters()[1].ParameterType;

        var navCurrentThread = metaTableType?.Assembly
            .GetType("Microsoft.Dynamics.Nav.Runtime.NavCurrentThread");
        // ResolveAppGroup is overloaded; the no-arg one is [Obsolete] and much more expensive.
        // Bind the session-taking overload by its parameter type, not by arity alone.
        _mResolveAppGroup = navCurrentThread?.GetMethods(BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "ResolveAppGroup"
                                 && m.GetParameters().Length == 1
                                 && m.ReturnType == appGroupType);

        // _navTriggerEventType is checked HERE, not left to follow from _mMtIsEventSubscribed.
        // It does follow today — but a guard that is correct only by a neighbour's construction
        // is one edit away from permitting a Truncate() BC refuses, and that edit is silent.
        if (_pRecSession == null || _mMtIsEventSubscribed == null || _mResolveAppGroup == null
            || _navTriggerEventType == null)
            throw new AlRunner.Infrastructure.BcShapeGapException(
                "Record.Truncate()",
                "NCLMetaTable.IsEventSubscribed / NavCurrentThread.ResolveAppGroup",
                $"could not bind the delete-subscriber guard (Session={_pRecSession != null}, "
                + $"TriggerEventType={_navTriggerEventType != null}, "
                + $"IsEventSubscribed={_mMtIsEventSubscribed != null}, "
                + $"ResolveAppGroup={_mResolveAppGroup != null})");
    }

    /// <summary>
    /// Binds guard 7's chain: <c>RecordImplementation → TableState → FiltersAndMarks →
    /// {MarkedRecords, Filters} → {IsCompleteExpressionLarge, AnyFiltersOnFlowFields}</c>.
    ///
    /// <para>Every hop goes through <see cref="AlRunner.Infrastructure.BcShape.Property"/>, so a
    /// member BC has renamed refuses AT THAT HOP and names it. The shape this replaced was a
    /// chain of <c>?.</c> lookups: a null anywhere along it propagated to the end, guard 7 then
    /// read "no marks, no FlowField filters", and a <c>Truncate()</c> BC refuses **succeeded**
    /// — the silent-fake this very file exists to remove, one level down
    /// (.claude/rules/guards-need-a-third-state.md § "A reflection bind that answers null is
    /// unmeasurable, not absent"; caught by SilentReflectionLookupRatchetTests, #3663).</para>
    ///
    /// <para>Trap: the per-hop refusal is what makes the message useful — an aggregated check
    /// after six <c>?.</c> hops can say only "something in the chain moved", and BC renaming
    /// <c>Filters</c> would read identically to BC renaming <c>TableState</c>.</para>
    /// </summary>
    private static void ResolveGuard7Shape(Type recordType)
    {
        const string Surface = "Record.Truncate() (marks / FlowField-filter guard)";

        // The first hop is up the record's own hierarchy — NavRecord declares
        // RecordImplementation, but the runtime type is an AL-emitted subclass.
        _pRecRecordImplementation = FindPropertyUpHierarchy(recordType, "RecordImplementation")
            ?? throw new AlRunner.Infrastructure.BcShapeGapException(
                Surface, $"{recordType.Name}.RecordImplementation",
                "property not found on the record type or any base — BC's record layout moved");

        _pImplTableState = AlRunner.Infrastructure.BcShape.Property(
            _pRecRecordImplementation.PropertyType, "TableState", AnyInstance, Surface);
        _pTsFiltersAndMarks = AlRunner.Infrastructure.BcShape.Property(
            _pImplTableState.PropertyType, "FiltersAndMarks", AnyInstance, Surface);
        _pFamMarkedRecords = AlRunner.Infrastructure.BcShape.Property(
            _pTsFiltersAndMarks.PropertyType, "MarkedRecords", AnyInstance, Surface);
        _pFamFilters = AlRunner.Infrastructure.BcShape.Property(
            _pTsFiltersAndMarks.PropertyType, "Filters", AnyInstance, Surface);
        _pMrIsCompleteExpressionLarge = AlRunner.Infrastructure.BcShape.Property(
            _pFamMarkedRecords.PropertyType, "IsCompleteExpressionLarge", AnyInstance, Surface);
        _pFfdAnyFiltersOnFlowFields = AlRunner.Infrastructure.BcShape.Property(
            _pFamFilters.PropertyType, "AnyFiltersOnFlowFields", AnyInstance, Surface);
    }

    private static PropertyInfo? FindPropertyUpHierarchy(Type? t, string name)
    {
        while (t != null)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                        | BindingFlags.DeclaredOnly);
            if (p != null) return p;
            t = t.BaseType;
        }
        return null;
    }
}
