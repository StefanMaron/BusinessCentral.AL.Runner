// MethodScopeFlagSelectionTests — issues #4368 and #4365.
//
// The runner replaces NavMethodScope..ctor(NavApplicationObjectBase, MethodScopeFlags, bool)
// wholesale, so it owns the flag selection BC's own ctor performs. It had that selection
// INVERTED: it ignored the flags argument and kept only the virtual GetMethodScopeFlags(),
// where BC prefers the argument and falls back to the virtual call. It also dropped BC's
// try-scope inheritance entirely.
//
// The AL-OBSERVABLE half of #4368 — a trigger frame in a call stack is marked "(Trigger)" —
// is adjudicated upstream by a real service tier (corpus codeunit 60211,
// CallStack_ErrorInsideOnRunTrigger_MarksTheFrameAsATrigger and its negative twin), because
// it is a statement about BC that would be meaningful without the runner existing. What is
// proven HERE is only what is about the runner's own replacement:
//
//   - SelectMethodScopeFlags implements BC's precedence, argument first (#4368);
//   - InheritIsInTryScope reproduces BC's `parentScope.IsInTryScope && !IsRootScope` OR,
//     so a frame nested inside a try scope inherits the bit (#4368);
//   - the GetMethodScopeFlags bind is REQUIRED, so an unresolvable lookup refuses with
//     BcShapeGapException instead of reaching ordinal-0 flags through a `??` (#4365).
//
// These drive the private helpers directly. They read nothing from a live scope chain — the
// selection is a pure function of (argument, scope type) and the inheritance a pure function
// of (flags, parent flags) — so no BC engine and no bc-engine-serial membership is needed.
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class MethodScopeFlagSelectionTests
{
    private static Type FlagsEnumType()
        => typeof(NavMethodScope).GetField("flags", BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType
           ?? throw new InvalidOperationException("NavMethodScope.flags not found — Ncl shape changed.");

    private static object Flag(string name) => Enum.Parse(FlagsEnumType(), name);

    private static long Ordinal(object flags) => Convert.ToInt64(flags);

    private static MethodInfo Private(string name)
        => typeof(BcRuntime).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"BcRuntime.{name} not found.");

    /// <summary>A NavMethodScope instance with no constructor run — enough for the helpers,
    /// which read only its type and, for the parent, its flags field.</summary>
    private static NavMethodScope BareScope(Type scopeType)
        => (NavMethodScope)RuntimeHelpers.GetUninitializedObject(scopeType);

    private static object Select(NavMethodScope self, object? flagsArgument)
        => Private("SelectMethodScopeFlags").Invoke(null, new object?[] { self, flagsArgument })!;

    private static FieldInfo FlagsField()
        => typeof(NavMethodScope).GetField("flags", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static object Inherit(object scopeFlags, NavMethodScope self, object? parent)
        => Private("InheritIsInTryScope")
               .Invoke(null, new object?[] { FlagsField(), scopeFlags, self, parent })!;

    // ── BC's precedence: the ARGUMENT wins when non-zero (#4368) ─────────────────

    // The claim the inverted implementation got backwards. BC's ctor IL is
    // `flags = (flags == None) ? GetMethodScopeFlags() : flags`, so a caller passing
    // IsTrigger gets IsTrigger — not whatever the scope type's virtual call answers.
    //
    // This is the load-bearing direction: IsTrigger (64) and IsTest (128) reach the field
    // ONLY through the argument, because no GetMethodScopeFlags override in any provisioned
    // binary returns either (#4368, measured on 28.1.49838.53910 and 27.0.38460.53934). So an
    // implementation preferring the virtual call makes both unreachable.
    [Theory]
    [InlineData("IsTrigger")]
    [InlineData("IsTest")]
    public void ANonZeroFlagsArgument_WinsOverTheVirtualCall(string flagName)
    {
        var argument = Flag(flagName);
        var scope = BareScope(typeof(NavMethodScope<NavRecord>));

        var selected = Select(scope, argument);

        Assert.Equal(Ordinal(argument), Ordinal(selected));

        // And the virtual call really would have answered something else, so the assertion
        // above is a genuine preference rather than two routes coinciding.
        var virtualAnswer = BcRuntime.ResolveGetMethodScopeFlags(scope.GetType())!.Invoke(scope, null)!;
        Assert.NotEqual(Ordinal(argument), Ordinal(virtualAnswer));
    }

    // The fallback arm. A zero argument is BC's "None", which means "ask the scope type" —
    // this is the arm the old implementation was permanently stuck on.
    [Fact]
    public void AZeroFlagsArgument_FallsBackToTheVirtualCall()
    {
        var scope = BareScope(typeof(NavMethodScope<NavRecord>));
        var none = Enum.ToObject(FlagsEnumType(), 0);

        var selected = Select(scope, none);
        var virtualAnswer = BcRuntime.ResolveGetMethodScopeFlags(scope.GetType())!.Invoke(scope, null)!;

        Assert.Equal(Ordinal(virtualAnswer), Ordinal(selected));
        // NavMethodScope<TParent> answers IsStackFrame, so the fallback is not vacuously zero —
        // without this the test would pass against an implementation that always returned 0.
        Assert.NotEqual(0, Ordinal(selected));
    }

    // ── BC's try-scope inheritance (#4368) ──────────────────────────────────────

    // BC's ctor ORs IsInTryScope in from the PARENT. Without it the bit is set only on a
    // literal TryMethodScope and never on a frame nested inside one, which is the state
    // NavMethodScopeCtorReplacement's own doc comment names as load-bearing.
    [Fact]
    public void AParentInATryScope_PassesIsInTryScopeToTheChild()
    {
        var flagsField = FlagsField();
        var child = BareScope(typeof(NavMethodScope<NavRecord>));
        var parent = BareScope(typeof(NavMethodScope<NavRecord>));
        flagsField.SetValue(parent, Flag("IsInTryScope"));

        var childOwn = Flag("IsStackFrame");
        var result = Inherit(childOwn, child, parent);

        Assert.Equal(
            Ordinal(childOwn) | Ordinal(Flag("IsInTryScope")),
            Ordinal(result));
        // The child's own bit survives the OR rather than being replaced by the parent's.
        Assert.NotEqual(Ordinal(Flag("IsInTryScope")), Ordinal(result));
    }

    // The negative arm: a parent NOT in a try scope must not confer the bit. Without this a
    // fix that unconditionally ORed IsInTryScope in would pass the positive test.
    [Fact]
    public void AParentNotInATryScope_ConfersNothing()
    {
        var flagsField = FlagsField();
        var child = BareScope(typeof(NavMethodScope<NavRecord>));
        var parent = BareScope(typeof(NavMethodScope<NavRecord>));
        flagsField.SetValue(parent, Flag("IsStackFrame"));

        var childOwn = Flag("IsStackFrame");
        var result = Inherit(childOwn, child, parent);

        Assert.Equal(Ordinal(childOwn), Ordinal(result));
        Assert.Equal(0, Ordinal(result) & Ordinal(Flag("IsInTryScope")));
    }

    // BC's `!IsRootScope` term. The runner's root is the pre-built _skeletonRootScope and
    // never a ctor-replacement product, so this cannot fire today — it is pinned so a future
    // root-scope path cannot silently start inheriting the bit.
    [Fact]
    public void AScopeThatIsItsOwnParent_DoesNotInheritFromItself()
    {
        var flagsField = FlagsField();
        var scope = BareScope(typeof(NavMethodScope<NavRecord>));
        flagsField.SetValue(scope, Flag("IsInTryScope"));

        var result = Inherit(Flag("IsStackFrame"), scope, scope);

        Assert.Equal(Ordinal(Flag("IsStackFrame")), Ordinal(result));
    }

    // ── #4365: the bind is required, so an unresolvable lookup refuses ──────────

    // #4365 reported three routes to ordinal-0 flags. All three are measured UNREACHABLE on
    // every provisioned build — GetMethodScopeFlags is `virtual protected` on NavMethodScope
    // itself and GetMethod(NonPublic | Instance) resolves protected base members, and the
    // return type is a non-nullable enum so a successful Invoke cannot be null. What remains
    // is the one genuinely unmeasurable route: BC renaming or removing the member. That must
    // refuse rather than answer 0, because ordinal-0 at the field is indistinguishable from a
    // scope that genuinely has no flags (guards-need-a-third-state.md).
    private sealed class ScopeTypeWithoutTheMember { }

    [Fact]
    public void AScopeTypeNotDeclaringGetMethodScopeFlags_RefusesRatherThanAnsweringZero()
    {
        BcRuntime.ResetGetMethodScopeFlagsCacheForTests();
        try
        {
            var ex = Assert.Throws<BcShapeGapException>(
                () => BcRuntime.ResolveGetMethodScopeFlags(typeof(ScopeTypeWithoutTheMember)));

            // The refusal must name the member, so a reader knows which BC shape moved.
            Assert.Contains("GetMethodScopeFlags", ex.Message, StringComparison.Ordinal);
        }
        finally { BcRuntime.ResetGetMethodScopeFlagsCacheForTests(); }
    }

    // The counterpart: on the real BC type the bind SUCCEEDS, so the refusal above is about
    // an absent member and not about the lookup being broken for everything.
    [Fact]
    public void TheRealNavMethodScopeType_StillResolvesGetMethodScopeFlags()
    {
        BcRuntime.ResetGetMethodScopeFlagsCacheForTests();
        try
        {
            var resolved = BcRuntime.ResolveGetMethodScopeFlags(typeof(NavMethodScope<NavRecord>));

            Assert.NotNull(resolved);
            Assert.Equal("GetMethodScopeFlags", resolved!.Name);
            // A non-nullable enum return is what makes the `??` #4365 flagged unreachable.
            Assert.True(resolved.ReturnType.IsEnum);
            Assert.False(Nullable.GetUnderlyingType(resolved.ReturnType) != null);
        }
        finally { BcRuntime.ResetGetMethodScopeFlagsCacheForTests(); }
    }
}
