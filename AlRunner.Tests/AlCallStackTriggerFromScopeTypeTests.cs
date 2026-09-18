// AlCallStackTriggerFromScopeTypeTests — issue #4368.
//
// AlCallStackCapture decided "is this frame a trigger" from NavMethodScope.flags alone. BC
// decides it from the scope TYPE first: CallStackElement.GetText appends "(Trigger)" from
// MethodSourceInfo.IsTrigger, whose first arm walks the scope type's base chain looking for
// NavTriggerMethodScope<>, and only falls back to MethodScopeFlags when the scope type is
// unavailable.
//
// The flags route cannot answer true for an AL-emitted scope, so reading it alone rendered
// EVERY trigger frame unmarked. Measured on 28.1.49838.53910: NavTriggerMethodScope<TParent>
// is a marker type that chains base(applicationObject) -> NavMethodScope(TParent, bool) ->
// NavMethodScope(obj, MethodScopeFlags.None, eventSource), so BC's own ctor takes the
// GetMethodScopeFlags() branch, and no override of that method returns IsTrigger.
//
// The BC-BEHAVIOUR claim — an AL call stack marks a trigger frame "(Trigger)" and a plain
// procedure frame not — is adjudicated upstream by a real service tier, in corpus codeunit
// 60211. What this file proves is the runner-side mechanism: the decision is taken from the
// scope type, both directions, and the flags fallback still works for a non-trigger type.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class AlCallStackTriggerFromScopeTypeTests
{
    private static MethodInfo IsScopeTypeATriggerMethod()
        => typeof(AlCallStackCapture).GetMethod(
               "IsScopeTypeATrigger", BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException("AlCallStackCapture.IsScopeTypeATrigger not found.");

    private static bool IsTriggerType(Type? t)
        => (bool)IsScopeTypeATriggerMethod().Invoke(null, new object?[] { t })!;

    /// <summary>A subclass of BC's trigger scope that is NOT itself the generic type, so the
    /// walk has to climb rather than match at depth zero — the shape an AL-emitted trigger
    /// scope actually has (measured: `Record60900+OnInsert_Scope` derives from
    /// `NavTriggerMethodScope&lt;Record60900&gt;`).</summary>
    private sealed class DerivedTriggerScope : NavTriggerMethodScope<NavRecord>
    {
        private DerivedTriggerScope() : base(null!) { }
    }

    /// <summary>The same one level down again, so a walk that only looked at BaseType would
    /// fail here while passing the row above.</summary>
    private sealed class DoublyDerivedTriggerScope : IntermediateTriggerScope
    {
        private DoublyDerivedTriggerScope() : base() { }
    }

    internal class IntermediateTriggerScope : NavTriggerMethodScope<NavRecord>
    {
        protected IntermediateTriggerScope() : base(null!) { }
    }

    private sealed class DerivedPlainScope : NavMethodScope<NavRecord>
    {
        private DerivedPlainScope() : base(null!) { }
    }

    // ── The positive direction: a trigger scope type, at three depths ────────────

    [Theory]
    [InlineData(typeof(DerivedTriggerScope))]
    [InlineData(typeof(DoublyDerivedTriggerScope))]
    public void AScopeTypeDerivingFromNavTriggerMethodScope_IsATrigger(Type scopeType)
        => Assert.True(IsTriggerType(scopeType),
            $"{scopeType.Name} derives from NavTriggerMethodScope<> and must read as a trigger.");

    [Fact]
    public void TheOpenTriggerScopeTypeItself_IsATrigger()
        => Assert.True(IsTriggerType(typeof(NavTriggerMethodScope<NavRecord>)));

    // ── The negative direction ───────────────────────────────────────────────────
    //
    // Without these a walk that answered true unconditionally — or one that stopped at the
    // wrong base type and never terminated its search correctly — would pass everything above.

    [Theory]
    [InlineData(typeof(DerivedPlainScope))]
    [InlineData(typeof(NavMethodScope<NavRecord>))]
    [InlineData(typeof(NavMethodScope))]
    public void AScopeTypeNotDerivingFromNavTriggerMethodScope_IsNotATrigger(Type scopeType)
        => Assert.False(IsTriggerType(scopeType),
            $"{scopeType.Name} does not derive from NavTriggerMethodScope<> and must not read as a trigger.");

    [Fact]
    public void ANullScopeType_IsNotATrigger() => Assert.False(IsTriggerType(null));

    // A type entirely outside the NavMethodScope hierarchy must terminate rather than loop or
    // throw: the walk's stop condition is `t != typeof(NavMethodScope)`, which such a type
    // never reaches, so it relies on running out of base types instead.
    [Fact]
    public void ATypeOutsideTheScopeHierarchy_TerminatesAndIsNotATrigger()
        => Assert.False(IsTriggerType(typeof(string)));

    // ── BC's own rule, pinned against BC's own binary ────────────────────────────
    //
    // The whole fix rests on NavTriggerMethodScope<> being a MARKER — it adds no
    // GetMethodScopeFlags override, which is why the flags route can never answer true and
    // the type walk is the only thing that can. If Microsoft gave it an override, the fallback
    // would start working and this test names the build where that changed.
    [Fact]
    public void NavTriggerMethodScope_DeclaresNoGetMethodScopeFlagsOverride()
    {
        var declared = typeof(NavTriggerMethodScope<>).GetMethod(
            "GetMethodScopeFlags",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Null(declared);
    }

    // And the corollary that makes the walk necessary rather than merely faithful: an
    // instance of a trigger scope type reports flags WITHOUT IsTrigger through the virtual
    // call, so a reader consulting only the flags gets `false` for a real trigger frame.
    [Fact]
    public void ATriggerScopesVirtualFlags_DoNotCarryIsTrigger()
    {
        var scope = (NavMethodScope)RuntimeHelpers.GetUninitializedObject(typeof(DerivedTriggerScope));
        var flagsField = typeof(NavMethodScope).GetField("flags", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var isTrigger = Convert.ToInt64(flagsField.FieldType.GetField("IsTrigger")!.GetRawConstantValue());

        var virtualAnswer = Convert.ToInt64(
            BcRuntime.ResolveGetMethodScopeFlags(scope.GetType()).Invoke(scope, null)!);

        Assert.Equal(0, virtualAnswer & isTrigger);
    }
}
