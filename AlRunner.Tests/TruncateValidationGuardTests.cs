// TruncateValidationGuardTests — issue #4371.
//
// NavRecord.ValidateTruncateSupport is the whole precondition check for Record.Truncate().
// The runner replaced it with NoOp_OneArg, so all seven of BC's guards were discarded and
// Truncate() succeeded inside a try function where real BC refuses it. The replacement now
// keeps six of them and skips only the security-filter guard, which reads skeleton state that
// is not populated.
//
// The AL-OBSERVABLE half — "Record.Truncate() inside a try function raises
// 'Truncate is not supported in try functions.'" — is adjudicated upstream by a real service
// tier (corpus codeunit 60923, Truncate_NestedInsideATryFunction_IsRefused and its two
// siblings), because it is a statement about BC that would be meaningful without the runner
// existing. What is proven HERE is only what is about the runner's own replacement:
//
//   - the registered Cecil target is the faithful helper, not a no-op, so the guards run
//     at all;
//   - EnterTryScope sets IsInTryScope and ExitTryScope restores EXACTLY the prior flags,
//     so one try function cannot leave the rest of the run inside a try scope;
//   - a nested EnterTryScope does not clear the outer one's bit on exit.
//
// The scope helpers are pure functions of the reflected flags field, so these drive them
// through a hand-built scope object rather than a live session: no BC engine needed, and no
// bc-engine-serial membership.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class TruncateValidationGuardTests
{
    private static FieldInfo FlagsField()
        => typeof(NavMethodScope).GetField("flags", BindingFlags.NonPublic | BindingFlags.Instance)
           ?? throw new InvalidOperationException("NavMethodScope.flags not found — Ncl shape changed.");

    private static object Flag(string name) => Enum.Parse(FlagsField().FieldType, name);

    private static long Ordinal(object flags) => Convert.ToInt64(flags);

    // ── The Cecil registration: the guards must actually be wired to run ────────────────────

    // Without this the whole fix is inert: the helper below can be perfect and every guard
    // still never executes, because the rewrite still points ValidateTruncateSupport at a
    // no-op. This reads the replacement the runner exposes rather than the rewrite table,
    // which is what a caller ends up invoking.
    [Fact]
    public void ValidateTruncateSupport_IsReplacedByTheFaithfulHelper_NotANoOp()
    {
        var helper = typeof(BcRuntime).GetMethod(
            "NavRecord_ValidateTruncateSupport",
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(helper);
        // One argument, matching ValidateTruncateSupport(NavRecord) — a static replacement for
        // a static one-arg method takes no extra receiver slot.
        Assert.Single(helper!.GetParameters());
        Assert.Equal(typeof(void), helper.ReturnType);

        // A no-op body is what this fix removes, so pin that the helper is not one. The IL of a
        // `{ }` method body is 1 byte (ret); anything that reads a guard is larger.
        var il = helper.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);
        Assert.True(il!.Length > 1,
            $"NavRecord_ValidateTruncateSupport has a {il.Length}-byte body — that is a no-op, " +
            "which is the defect #4371 fixed.");
    }

    // ── EnterTryScope / ExitTryScope ────────────────────────────────────────────────────────

    private static MethodInfo Private(string name)
        => typeof(BcRuntime).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"BcRuntime.{name} not found.");

    // BC's TryInvoke wraps the body in `using (…GetTryMethodScope())`, so the flag is set for the
    // body and cleared on the way out. The runner sets it on the scope already current, and the
    // restore has to put back exactly what was there — a scope carries other flags (IsStackFrame,
    // IsTest, …) and clearing the field wholesale would drop them.
    [Fact]
    public void ExitTryScope_RestoresTheExactPriorFlags_NotJustTheTryBit()
    {
        var flagsField = FlagsField();
        var scope = BareScope();
        var before = Flag("IsStackFrame");
        flagsField.SetValue(scope, before);

        using var session = SessionCurrentScope(scope);
        var token = EnterOn(scope);
        var during = flagsField.GetValue(scope)!;
        ExitOn(token);
        var after = flagsField.GetValue(scope)!;

        // The bit went on for the body...
        Assert.Equal(Ordinal(before) | Ordinal(Flag("IsInTryScope")), Ordinal(during));
        // ...and the scope came back byte-for-byte, keeping IsStackFrame.
        Assert.Equal(Ordinal(before), Ordinal(after));
        Assert.NotEqual(Ordinal(during), Ordinal(after));
    }

    // A try function calling another try function: the inner exit must not clear a bit the OUTER
    // one still owns, or the remainder of the outer body silently leaves the try scope and
    // Record.Truncate() stops being refused there.
    [Fact]
    public void ANestedEnterTryScope_DoesNotClearTheOuterBit_OnExit()
    {
        var flagsField = FlagsField();
        var scope = BareScope();
        flagsField.SetValue(scope, Flag("IsStackFrame"));

        using var session = SessionCurrentScope(scope);
        var outer = EnterOn(scope);
        var inner = EnterOn(scope);   // already in a try scope: nothing to do, nothing to restore
        ExitOn(inner);

        var afterInner = flagsField.GetValue(scope)!;
        Assert.True((Ordinal(afterInner) & Ordinal(Flag("IsInTryScope"))) != 0,
            "the inner try function's exit cleared the outer one's IsInTryScope bit");

        ExitOn(outer);
        Assert.Equal(Ordinal(Flag("IsStackFrame")), Ordinal(flagsField.GetValue(scope)!));
    }

    // The nested case is a no-op by construction, and that is what makes the test above hold.
    // Pinned separately so a future EnterTryScope that "helpfully" returns a live token — and
    // therefore restores on the inner exit — fails here rather than only in the AL corpus.
    [Fact]
    public void EnterTryScope_ReturnsNothingToRestore_WhenAlreadyInATryScope()
    {
        var flagsField = FlagsField();
        var scope = BareScope();
        flagsField.SetValue(scope, Enum.ToObject(
            flagsField.FieldType,
            Ordinal(Flag("IsStackFrame")) | Ordinal(Flag("IsInTryScope"))));

        using var session = SessionCurrentScope(scope);
        Assert.Null(EnterOn(scope));
    }

    // ── plumbing ────────────────────────────────────────────────────────────────────────────

    // EnterOn/ExitOn invoke the PRODUCTION EnterTryScope/ExitTryScope. They do not reimplement
    // the flag transition: a helper that recomputed it here would pass with the real methods
    // deleted, which is a pin that reads its own copy rather than the shipped code.
    //
    // What they supply is only the state those methods read — BcRuntime's reflected-field and
    // skeleton-session statics, which ApplyAllPatches normally populates against a live engine.
    // Pointing _skeletonSession at a stand-in whose CurrentMethodScope is the caller's scope is
    // what lets these run without one.
    // Trap: ExitTryScope reads _fMsFlags too, so the swap must stay in place across BOTH calls.
    // Disposing it between them makes the restore a silent no-op — the field reads null and the
    // method returns — which surfaces as "the flags were never restored" and looks exactly like
    // a defect in ExitTryScope. Each test therefore runs its whole enter/exit sequence inside
    // one WithSession block.
    private static (object Scope, object Original)? EnterOn(object scope)
        => ((object Scope, object Original)?)Private("EnterTryScope")
            .Invoke(null, Array.Empty<object>());

    private static void ExitOn((object Scope, object Original)? token)
        => Private("ExitTryScope").Invoke(null, new object?[] { token });

    /// <summary>
    /// Points BcRuntime's <c>_fMsFlags</c> / <c>_fSessCurrentScope</c> / <c>_skeletonSession</c>
    /// statics at a one-field stand-in holding <paramref name="scope"/>, and restores every one
    /// of them on dispose so a test cannot leak session state into its neighbours.
    /// </summary>
    private static IDisposable SessionCurrentScope(object scope)
    {
        var holder = new ScopeHolder { CurrentMethodScope = scope };
        return new StaticSwap(
            (Static("_fMsFlags"), FlagsField()),
            (Static("_fSessCurrentScope"), typeof(ScopeHolder).GetField(nameof(ScopeHolder.CurrentMethodScope))!),
            (Static("_skeletonSession"), holder));
    }

    private sealed class ScopeHolder { public object? CurrentMethodScope; }

    private static FieldInfo Static(string name)
        => typeof(BcRuntime).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"BcRuntime.{name} not found.");

    private sealed class StaticSwap : IDisposable
    {
        private readonly (FieldInfo Field, object? Previous)[] _saved;

        public StaticSwap(params (FieldInfo Field, object? Value)[] assignments)
        {
            _saved = assignments.Select(a => (a.Field, a.Field.GetValue(null))).ToArray();
            foreach (var (field, value) in assignments) field.SetValue(null, value);
        }

        public void Dispose()
        {
            foreach (var (field, previous) in _saved) field.SetValue(null, previous);
        }
    }

    private static object BareScope()
        => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(NavMethodScope<NavRecord>));
}
