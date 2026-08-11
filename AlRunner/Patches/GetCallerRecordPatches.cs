// GetCallerRecordPatches — faithful replacement for NavRecord.GetCallerRecord(NavSession).
//
// WHY THIS EXISTS (#1781 — nested Validate re-snapshots xRec)
// -------------------------------------------------------------------------------------------
// BC's NavRecord.ValidateAsync (the body behind `Rec.Validate(Field, Value)`) re-snapshots the
// before-image (`OldRecord.Assign(this)`, i.e. what AL sees as `xRec`) UNLESS the Validate call
// was made from WITHIN a trigger already running on that exact same record — the compiled AL
// for `trigger OnValidate() begin Validate(OtherField, X); end;` calls
// `Rec.ALValidateSafe(fieldNo, expectedType, value)`, whose 3-arg overload resolves its own
// caller via `GetCallerRecord(base.Session)`. When that caller turns out to BE the record
// being validated (`RecordImplementation.TableEquals(...)`), BC skips the re-snapshot so a
// nested Validate's own OnValidate trigger still sees xRec as it was BEFORE the outer Validate
// call — not the outer call's just-written new value.
//
// GetCallerRecord's real body reads `session.CurrentMethodScope.ApplicationObject` and, when
// that AL object is itself a Record/TableExtension/Form/PageExtension, unwraps it to the
// concrete NavRecord. The runner's `NavSession.CurrentMethodScope` GETTER is hard-coded to
// always return the flat `_skeletonRootScope` (see SessionPatches.GetCurrentMethodScopeReplacement)
// — a deliberate, narrowly-scoped simplification: most Ncl call sites off that getter only need
// SOME non-null scope to avoid an NRE, and handing back a real (thinly-populated) per-call scope
// object broke ~150 corpus tests when tried as a blanket fix (those call sites dereference
// OTHER NavMethodScope state — Diagnostics-adjacent chains, etc. — that
// MethodScopePatches.NavMethodScopeCtorReplacement deliberately leaves at default because the
// thin scope objects were only ever meant to satisfy StmtHit()/recursion-depth/leak bookkeeping,
// not to be a fully faithful NavMethodScope).
//
// GetCallerRecord is the ONE consumer that actually needs to know "what AL frame is running
// right now" to make a same-record-vs-different-record decision, so instead of changing the
// shared getter, this reads the ACTUAL tracked value directly off the backing field that
// MethodScopePatches.NavMethodScopeCtorReplacement/NavMethodScope_Dispose already push/pop on
// every AL call — bypassing the intentionally-flattened public getter for this one call site
// only. Every other consumer of NavSession.CurrentMethodScope is unaffected.
//
// Runtime-engine layer (Ncl.dll) — allowed to modify (see precompiled-dll-respect.md).
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Runtime.Extensions;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner;

public static partial class BcRuntime
{
    private static PropertyInfo? _pRecordExtensionParentObject; // NavRecordExtension.ParentObject (protected internal)
    private static PropertyInfo? _pFormExtensionParentObject;   // NavFormExtension.ParentObject (protected internal)
    private static bool _callerRecordHelpersResolved;

    private static void EnsureCallerRecordHelpers()
    {
        if (_callerRecordHelpersResolved) return;
        _callerRecordHelpersResolved = true;
        _pRecordExtensionParentObject = typeof(NavRecordExtension).GetProperty(
            "ParentObject", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        _pFormExtensionParentObject = typeof(NavFormExtension).GetProperty(
            "ParentObject", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
    }

    /// <summary>
    /// Replacement for the internal static <c>NavRecord.GetCallerRecord(NavSession)</c>.
    /// Mirrors the real body exactly (same switch over <c>ApplicationObject.NavType</c>), the
    /// only difference being WHERE the current scope comes from — see file header.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NavRecord? NavRecord_GetCallerRecord(NavSession session)
    {
        var scope = (_fSessCurrentScope?.GetValue(session) ?? _skeletonRootScope) as NavMethodScope;
        var applicationObject = scope?.ApplicationObject;
        if (applicationObject == null) return null;

        EnsureCallerRecordHelpers();
        switch (applicationObject.NavType)
        {
            case NavType.Record:
                return applicationObject as NavRecord;
            case NavType.TableExtension:
                return (_pRecordExtensionParentObject?.GetValue(applicationObject as NavRecordExtension)) as NavRecord;
            case NavType.Form:
                return (applicationObject as NavForm)?.SourceTable;
            case NavType.PageExtension:
                var parentForm = (_pFormExtensionParentObject?.GetValue(applicationObject as NavFormExtension)) as NavForm;
                return parentForm?.SourceTable;
            default:
                return null;
        }
    }
}
