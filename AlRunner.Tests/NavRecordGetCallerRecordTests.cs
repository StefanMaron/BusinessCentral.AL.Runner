// NavRecordGetCallerRecordTests — contract tests for BcRuntime.NavRecord_GetCallerRecord
// (see GetCallerRecordPatches.cs file header for the full #1781 root-cause story).
//
// This is deliberately NOT a claim about what Business Central does (that claim is proven
// upstream against real BC — StefanMaron/BusinessCentral.AL.Language.Tests#40 — per
// .claude/rules/bc-behavior-tests-go-upstream.md). It is a claim about OUR OWN reimplementation:
// that it correctly reads the tracked NavSession.CurrentMethodScope backing field (the one
// MethodScopePatches.NavMethodScopeCtorReplacement/NavMethodScope_Dispose push/pop on every AL
// call) and unwraps the current scope's ApplicationObject back to a NavRecord — instead of the
// flat _skeletonRootScope the shared NavSession.CurrentMethodScope GETTER always answers (see
// SessionPatches.GetCurrentMethodScopeReplacement). #1781's bug was that nothing else could ever
// tell a nested Validate() call (same record, mid-trigger) apart from an unrelated one, because
// this method always saw "no caller" (ApplicationObject == null).
//
// Deliberately does NOT load the BC engine / trigger the Ncl Cecil rewrite: it calls
// BcRuntime.NavRecord_GetCallerRecord directly (an ordinary C# static method, accessible via
// InternalsVisibleTo) against hand-built NavSession/NavRecord/NavMethodScope instances
// (RuntimeHelpers.GetUninitializedObject + direct field pokes — same technique as
// SkeletonSharedObjectContainerLeakTests, just without needing a rewritten Ncl.dll or a running
// engine at all). That keeps this test fast, deterministic, and focused purely on the mapping
// logic that was actually broken, mirroring MediaSetPatchesTests' "contract test" shape.
//
// RED/GREEN: deleting GetCallerRecordPatches.cs makes this file fail to COMPILE (the method
// under test no longer exists) — the strongest possible RED. Reverting just the method BODY to
// `=> null` (the old unconditional-null behaviour #1781 reports) makes
// CurrentScope_HasRecordApplicationObject_ReturnsThatRecord fail at runtime instead (asserts
// null != the pushed record).
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class NavRecordGetCallerRecordTests
{
    private static FieldInfo ResolveBcRuntimePrivateStaticField(string name)
        => typeof(BcRuntime).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"BcRuntime.{name} not found — did the field get renamed?");

    private static FieldInfo ResolveSessionCurrentScopeBackingField()
        => typeof(NavSession).GetField("<CurrentMethodScope>k__BackingField",
               BindingFlags.NonPublic | BindingFlags.Instance)
           ?? throw new InvalidOperationException(
               "NavSession.<CurrentMethodScope>k__BackingField not found — Ncl shape changed.");

    private static FieldInfo ResolveScopeParentBackingField()
        => typeof(NavMethodScope<NavRecord>).GetField("<Parent>k__BackingField",
               BindingFlags.NonPublic | BindingFlags.Instance)
           ?? throw new InvalidOperationException(
               "NavMethodScope<TParent>.<Parent>k__BackingField not found — Ncl shape changed.");

    /// <summary>
    /// Points BcRuntime's own cached "_fSessCurrentScope" FieldInfo at the real
    /// NavSession.CurrentMethodScope backing field for the duration of the test, restoring
    /// whatever was there before (null if BcRuntime.EnsureApplied() never ran in this process —
    /// this test never depends on that having happened).
    /// </summary>
    private static IDisposable PatchSessCurrentScopeField()
    {
        var bcRuntimeField = ResolveBcRuntimePrivateStaticField("_fSessCurrentScope");
        var original = bcRuntimeField.GetValue(null);
        bcRuntimeField.SetValue(null, ResolveSessionCurrentScopeBackingField());
        return new Restore(() => bcRuntimeField.SetValue(null, original));
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _action;
        public Restore(Action action) => _action = action;
        public void Dispose() => _action();
    }

    [Fact]
    public void CurrentScope_HasRecordApplicationObject_ReturnsThatRecord()
    {
        using var _ = PatchSessCurrentScopeField();

        var session = (NavSession)RuntimeHelpers.GetUninitializedObject(typeof(NavSession));
        var record = (NavRecord)RuntimeHelpers.GetUninitializedObject(typeof(NavRecord));

        // A blank NavRecord is enough: NclType (and therefore NavType, which
        // NavRecord_GetCallerRecord switches on) is a pure override reading no instance state.
        var scope = (NavMethodScope)RuntimeHelpers.GetUninitializedObject(typeof(NavTriggerMethodScope<NavRecord>));
        ResolveScopeParentBackingField().SetValue(scope, record);
        ResolveSessionCurrentScopeBackingField().SetValue(session, scope);

        var result = BcRuntime.NavRecord_GetCallerRecord(session);

        Assert.Same(record, result);
    }

    [Fact]
    public void CurrentScope_DifferentRecordApplicationObject_ReturnsThatOtherRecord_NotNull()
    {
        // Negative-shape companion to the positive case above: proves the method returns
        // the ACTUAL current-scope record, not a hard-coded / cached one from a previous call.
        using var _ = PatchSessCurrentScopeField();

        var session = (NavSession)RuntimeHelpers.GetUninitializedObject(typeof(NavSession));
        var recordA = (NavRecord)RuntimeHelpers.GetUninitializedObject(typeof(NavRecord));
        var recordB = (NavRecord)RuntimeHelpers.GetUninitializedObject(typeof(NavRecord));

        var scopeA = (NavMethodScope)RuntimeHelpers.GetUninitializedObject(typeof(NavTriggerMethodScope<NavRecord>));
        ResolveScopeParentBackingField().SetValue(scopeA, recordA);
        var scopeB = (NavMethodScope)RuntimeHelpers.GetUninitializedObject(typeof(NavTriggerMethodScope<NavRecord>));
        ResolveScopeParentBackingField().SetValue(scopeB, recordB);

        ResolveSessionCurrentScopeBackingField().SetValue(session, scopeA);
        Assert.Same(recordA, BcRuntime.NavRecord_GetCallerRecord(session));

        ResolveSessionCurrentScopeBackingField().SetValue(session, scopeB);
        var result = BcRuntime.NavRecord_GetCallerRecord(session);

        Assert.Same(recordB, result);
        Assert.NotSame(recordA, result);
    }

    [Fact]
    public void NoScopePushed_ReturnsNull()
    {
        // #1781's exact starting condition — no caller record tracked at all — must still
        // answer null rather than throw or invent a caller.
        using var _ = PatchSessCurrentScopeField();

        var session = (NavSession)RuntimeHelpers.GetUninitializedObject(typeof(NavSession));
        ResolveSessionCurrentScopeBackingField().SetValue(session, null);

        var result = BcRuntime.NavRecord_GetCallerRecord(session);

        Assert.Null(result);
    }
}
