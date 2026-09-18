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

        // Guard 5 — OnBeforeDelete/OnAfterDelete subscribers. Skipped rather than faked: the
        // check is NCLMetaTable.IsEventSubscribed(NavTriggerEventType, NavAppGroup), whose second
        // argument comes from NavCurrentThread.ResolveAppGroup — the skeleton app-group resolution
        // NavApplicationObjectBaseCtorReplacement deliberately does not perform (it pins
        // BaseGroupId = 0), so any answer here would be about the runner's placeholder group
        // rather than about BC. Tracked by #4374.

        // Guard 6 — MediaFieldCount.
        if (metaTable != null && _pMtMediaFieldCount?.GetValue(metaTable) is int mediaFields && mediaFields > 0)
            ThrowTruncate("Truncate is not supported when the field has Media and/or MediaSet fields.");

        // Guard 7 — marked records / FlowField filters. Skipped: both read
        // RecordImplementation.TableState.FiltersAndMarks, which the runner's in-memory provider
        // does not populate in the shape BC's IsCompleteExpressionLarge and
        // FlowFieldsHelper.AnyFiltersOnFlowFields read. Tracked by #4374.
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

        var ncl = recordType.Assembly;
        var metaTableType = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable");
        _pMtSupportsTruncation = metaTableType?.GetProperty("SupportsTruncation");
        _pMtMediaFieldCount = metaTableType?.GetProperty("MediaFieldCount");

        // The exception lives in Types.dll, not Ncl.dll, so it is resolved by name across the
        // loaded set rather than off `ncl`.
        _navCSideTruncateExceptionType =
            Type.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavCSideTruncateException, Microsoft.Dynamics.Nav.Types")
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavCSideTruncateException"))
                .FirstOrDefault(t => t != null);
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
