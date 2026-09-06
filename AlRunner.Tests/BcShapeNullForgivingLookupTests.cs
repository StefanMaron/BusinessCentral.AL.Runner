// BcShapeNullForgivingLookupTests — the behavioural half of #3051.
//
// THE DEFECT, AND WHY IT INVERTS RESULTS RATHER THAN HIDING THEM
//   `t.GetProperty("X")!` is a compiler annotation. It emits no code and it throws nothing.
//   When Microsoft moves X the lookup returns a silent null and the NullReferenceException
//   lands at the first USE — `.PropertyType`, `.GetValue`, `.Invoke` — on a line that no
//   longer names X. MethodScopePatches.NavMethodScope_AssertError is an unfiltered
//   catch(Exception), so on any AL-entered path that NRE is SWALLOWED and `asserterror`
//   PASSES on a read real BC performs fine. That is the opposite of BC's answer, in green.
//
//   The two arms named *_SwallowsTheNre_WhichIsTheDefect below MEASURE that, against the
//   production seams, using the same `!` shape the 73 converted sites used to have. They are
//   the reason this file can claim the conversion buys something: without them "BcShape tears
//   through" would be a statement with nothing to contrast against.
//
// WHAT IS PROVED HERE
//   1. Each BcShape overload raises a BcShapeGapException naming `Declaring.Member` when the
//      read cannot be performed, and returns the real member — unchanged — when it can. Every
//      refusal arm is paired with a control, so a helper that threw unconditionally would fail
//      here rather than pass.
//   2. The overloads resolve EXACTLY what the `!` site resolved. The flags-fidelity arms are
//      the load-bearing ones: converting 73 sites would be a behaviour change if a helper
//      quietly widened Public|Instance to Public|NonPublic|Instance, because a non-public
//      member could then start shadowing the public one the site had been reading.
//   3. Both AL seams tear through a BcShapeGapException — with a control proving the seams
//      still trap what they are supposed to trap, so "tears through" is not vacuous.
//   4. Four PRODUCTION call sites converted by #3051, driven with a fake type standing in for
//      a BC type whose member has moved. These are the arms that fail on an unfixed tree: they
//      get a NullReferenceException (or, through NavMethodScope_AssertError, no exception at
//      all) instead of a BcShapeGapException naming the member.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcShapeNullForgivingLookupTests
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

    private const string Surface = "a BC surface under test";

    // ══ 1. Property ══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Property_RaisesAShapeGapNamingTheMember_WhenBcsPropertyHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcShape.Property(typeof(MovedShape), "GoneAway", PublicInstance, Surface));

        Assert.Equal(Surface, ex.Surface);
        Assert.Equal("MovedShape.GoneAway", ex.Member);
        Assert.Contains("property not found", ex.Detail, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(" — see docs/limitations.md#bc-shape-gaps", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Property_StillReturnsTheRealProperty_WhenItIsThere()
    {
        var p = BcShape.Property(typeof(MovedShape), "Present", PublicInstance, Surface);

        Assert.Equal("Present", p.Name);
        Assert.Equal(typeof(List<int>), p.PropertyType);
    }

    // The single-argument overload mirrors `GetProperty(name)`, whose default flags include
    // Static — so this is not the same question as the flags overload above.
    [Fact]
    public void Property_WithoutFlags_FindsAStaticProperty_AsGetPropertyNameWould()
        => Assert.Equal("StaticPresent", BcShape.Property(typeof(MovedShape), "StaticPresent", Surface).Name);

    /// <summary>
    /// The fidelity arm. `GetProperty(name, Public | Instance)` does NOT see a non-public
    /// member; a helper that widened to Public | NonPublic | Instance would find one, and 73
    /// converted sites would then be resolving a different member than before.
    /// </summary>
    [Fact]
    public void Property_DoesNotWidenTheFlags_SoAConvertedSiteResolvesWhatItAlwaysDid()
    {
        Assert.Throws<BcShapeGapException>(
            () => BcShape.Property(typeof(MovedShape), "Hidden", PublicInstance, Surface));

        Assert.Equal(typeof(string),
            BcShape.Property(typeof(MovedShape), "Hidden", BcShape.AnyInstance, Surface).PropertyType);
    }

    // ══ 2. Method ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Method_RaisesAShapeGapNamingTheMember_WhenBcsMethodHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcShape.Method(typeof(MovedShape), "GoneAway", BcShape.AnyInstance, Surface));

        Assert.Equal("MovedShape.GoneAway", ex.Member);
        Assert.Contains("method not found", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Method_StillReturnsTheRealMethod_WhenItIsThere()
        => Assert.Equal(1, BcShape.Method(typeof(MovedShape), "Take", BcShape.AnyInstance, Surface)
                                 .GetParameters().Length);

    /// <summary>
    /// The overload filter is part of the question: a method that keeps its name and changes
    /// its parameter list is the same "BC's layout moved" case as an absent one, and the
    /// message says which signature was looked for.
    /// </summary>
    [Fact]
    public void Method_WithASignature_RefusesWhenTheOverloadIsGone_AndNamesTheSignature()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcShape.Method(typeof(MovedShape), "Take", BcShape.AnyInstance,
                                 new[] { typeof(string), typeof(int) }, Surface));

        Assert.Equal("MovedShape.Take(String, Int32)", ex.Member);

        Assert.Equal(typeof(int),
            BcShape.Method(typeof(MovedShape), "Take", BcShape.AnyInstance, new[] { typeof(int) }, Surface)
                   .GetParameters()[0].ParameterType);
    }

    // ══ 3. Field, Constructor, NestedType ════════════════════════════════════════════════

    [Fact]
    public void Field_RaisesAShapeGapNamingTheMember_WhenBcsFieldHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcShape.Field(typeof(MovedShape), "goneAway", BcShape.AnyInstance, Surface));

        Assert.Equal("MovedShape.goneAway", ex.Member);
        Assert.Contains("field not found", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_StillReturnsTheRealField_WhenItIsThere()
        => Assert.Equal(typeof(int), BcShape.Field(typeof(MovedShape), "present", BcShape.AnyInstance, Surface).FieldType);

    [Fact]
    public void Constructor_RaisesAShapeGapNamingTheSignature_WhenBcsCtorHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcShape.Constructor(typeof(MovedShape), new[] { typeof(Guid) }, Surface));

        Assert.Equal("MovedShape..ctor(Guid)", ex.Member);
        Assert.Contains("constructor not found", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_StillReturnsTheRealCtor_AndTheNonPublicOverloadNeedsTheFlags()
    {
        Assert.Single(BcShape.Constructor(typeof(MovedShape), new[] { typeof(int) }, Surface).GetParameters());

        // GetConstructor(Type[]) is public-only, so the internal ctor is invisible to it and
        // visible to the flags overload — the same fidelity property the Property arm pins.
        Assert.Throws<BcShapeGapException>(
            () => BcShape.Constructor(typeof(MovedShape), new[] { typeof(string) }, Surface));
        Assert.Single(BcShape.Constructor(typeof(MovedShape), BcShape.AnyInstance,
                                          new[] { typeof(string) }, Surface).GetParameters());
    }

    [Fact]
    public void NestedType_RaisesAShapeGapNamingTheMember_AndReturnsTheRealOne()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcShape.NestedType(typeof(MovedShape), "GoneAway", BindingFlags.Public, Surface));
        Assert.Equal("MovedShape.GoneAway", ex.Member);
        Assert.Contains("nested type not found", ex.Detail, StringComparison.Ordinal);

        Assert.Equal(typeof(MovedShape.Inner),
            BcShape.NestedType(typeof(MovedShape), "Inner", BindingFlags.Public, Surface));
    }

    // ══ 4. The inversion, measured at both AL seams ══════════════════════════════════════

    /// <summary>
    /// The defect, reproduced against the production seam with the exact shape the 73
    /// converted sites had. `asserterror` sees no error at all: on real BC the read succeeds
    /// and the asserterror FAILS, so a green here is the opposite of BC's answer.
    /// </summary>
    [Fact]
    public void AssertError_SwallowsTheNre_WhichIsTheDefect()
    {
        var swallowed = false;
        BcRuntime.NavMethodScope_AssertError(null!, () =>
        {
            _ = typeof(MovedShape).GetProperty("GoneAway", PublicInstance)!.PropertyType;
            swallowed = true;                 // never reached; the NRE happens above
        });
        Assert.False(swallowed);              // it threw…
                                              // …and NavMethodScope_AssertError returned anyway.
    }

    /// <summary>
    /// MEASURED, and it narrows the issue's claim: the [TryFunction] seam does NOT swallow the
    /// NRE. NavApplicationObjectBase_TryInvoke swallows a trappable NavBaseException and a
    /// permanently out-of-scope refusal, and rethrows everything else — so a NullReferenceException
    /// tears through it. The inversion #3051 is about therefore lives at ONE seam, `asserterror`,
    /// not at both. That is worth pinning: it is the difference between "AL cannot see this" and
    /// "AL sees the wrong answer", and only the second is a green test that lies.
    /// </summary>
    [Fact]
    public void TryFunction_LetsTheNreThrough_SoTheInversionIsTheAssertErrorSeamOnly()
        => Assert.Throws<NullReferenceException>(() => BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => _ = typeof(MovedShape).GetProperty("GoneAway", PublicInstance)!.PropertyType));

    [Fact]
    public void AssertError_TearsThroughAShapeGap_InsteadOfSwallowingIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavMethodScope_AssertError(
                null!, () => BcShape.Property(typeof(MovedShape), "GoneAway", PublicInstance, Surface)));

        Assert.Equal("MovedShape.GoneAway", ex.Member);
    }

    [Fact]
    public void TryFunction_TearsThroughAShapeGap_InsteadOfSwallowingIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(
                null, () => BcShape.Method(typeof(MovedShape), "GoneAway", BcShape.AnyInstance, Surface)));

        Assert.Equal("MovedShape.GoneAway", ex.Member);
    }

    // CONTROL: the seams still trap what they are supposed to trap, so "tears through" above
    // is a statement about BcShapeGapException and not about seams that catch nothing.
    [Fact]
    public void BothSeams_StillTrapAPermanentRefusal_SoTearThroughIsNotVacuous()
    {
        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw new RunnerOutOfScopeException(
                "NavEmail.Send", "email-smtp — no SMTP transport in the runner", "email")));

        BcRuntime.NavMethodScope_AssertError(null!, () => throw new RunnerOutOfScopeException(
            "NavEmail.Send", "email-smtp — no SMTP transport in the runner", "email"));
    }

    // ══ 5. Four converted PRODUCTION call sites ══════════════════════════════════════════
    //
    // Each takes the BC object (or its type) as a parameter, so a fake standing in for a BC
    // type whose member has moved reaches the real code path without touching a BC install.
    // Every one of these fails on an unfixed tree with NullReferenceException instead.

    /// <summary>
    /// ExecutionSchedulerShutdown.DisposeIfRealized — `lazyType.GetProperty("Value", …)!`
    /// on BC's LazyEx&lt;ExecutionScheduler&gt;.
    /// </summary>
    [Fact]
    public void ExecutionSchedulerShutdown_RaisesAShapeGap_WhenLazyExValueHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => ExecutionSchedulerShutdown.DisposeIfRealized(new RealizedLazyWithoutValue()));

        Assert.Equal("RealizedLazyWithoutValue.Value", ex.Member);
        Assert.Equal("task-scheduler shutdown", ex.Surface);
    }

    // CONTROLS: the two answers that are NOT a shape gap still come back as answers. An
    // unrealized lazy must not even be asked for Value — reading it would start the very
    // scheduler thread this helper exists to stop.
    [Fact]
    public void ExecutionSchedulerShutdown_StillAnswers_WhenTheLazyIsIntact()
    {
        var realized = new RealizedLazy();
        Assert.Equal(ExecutionSchedulerShutdown.Outcome.Disposed,
                     ExecutionSchedulerShutdown.DisposeIfRealized(realized));
        Assert.True(realized.Disposed);

        Assert.Equal(ExecutionSchedulerShutdown.Outcome.NotRealized,
                     ExecutionSchedulerShutdown.DisposeIfRealized(new UnrealizedLazyWithoutValue()));
        Assert.Equal(ExecutionSchedulerShutdown.Outcome.NoEnvironment,
                     ExecutionSchedulerShutdown.DisposeIfRealized(null));
    }

    /// <summary>
    /// RunnerPageInstance.GetValue — `expression.GetType().GetMethod("Get", …)!` on BC's
    /// page-field expression object.
    /// </summary>
    [Fact]
    public void RunnerPageInstanceGetValue_RaisesAShapeGap_WhenTheExpressionsGetterHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => RunnerPageInstance.GetValue(new ExpressionWithoutGet()));

        // The signature is part of the member name: BC keeping `Get` while changing its
        // parameter list is the same "layout moved" case as removing it.
        Assert.Equal("ExpressionWithoutGet.Get()", ex.Member);
        Assert.Equal("TestPage field expression access", ex.Surface);
    }

    // …and through both AL seams, which is where the inversion lived.
    [Fact]
    public void RunnerPageInstanceGetValue_TearsThroughBothAlSeams()
    {
        Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavMethodScope_AssertError(null!, () => RunnerPageInstance.GetValue(new ExpressionWithoutGet())));
        Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => RunnerPageInstance.GetValue(new ExpressionWithoutGet())));
    }

    /// <summary>
    /// RecordPatches.GetList — `obj.GetType().GetProperty(name, …)!` while building a real
    /// NCLMetaQuery out of BC's own query metadata.
    /// </summary>
    [Fact]
    public void QueryMetadataGetList_RaisesAShapeGap_WhenBcsListPropertyHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => InvokeRecordPatches("GetList", new MetaQueryWithoutDataItems(), "DataItems"));

        Assert.Equal("MetaQueryWithoutDataItems.DataItems", ex.Member);
        Assert.Equal("AL query metadata construction", ex.Surface);
    }

    [Fact]
    public void QueryMetadataGetList_StillReturnsTheList_WhenThePropertyIsThere()
        => Assert.Equal(2, ((IList)InvokeRecordPatches("GetList", new MetaQueryWithDataItems(), "DataItems")!).Count);

    /// <summary>
    /// EventSubscriberPatches.ReplaceAttributeWithZeroedCopy — six consecutive
    /// `oType.GetProperty(…)!` reads on BC's NavEventSubscriber attribute.
    /// </summary>
    [Fact]
    public void EventSubscriberAttributeCopy_RaisesAShapeGap_WhenBcsAttributeHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => InvokeEventSubscriberPatches("ReplaceAttributeWithZeroedCopy",
                                               new SubscriberMethodInfo(new AttributeWithoutTargetObjectId())));

        Assert.Equal("AttributeWithoutTargetObjectId.TargetObjectId", ex.Member);
        Assert.Equal("event-subscriber inventory", ex.Surface);
    }

    // CONTROL: an attribute BC never set is an ANSWER, not a shape gap — the method returns
    // without raising, so the refusal above is about the member and not about the fake.
    [Fact]
    public void EventSubscriberAttributeCopy_ReturnsQuietly_WhenThereIsNoAttributeAtAll()
        => InvokeEventSubscriberPatches("ReplaceAttributeWithZeroedCopy", new SubscriberMethodInfo(null));

    // ══ Plumbing ═════════════════════════════════════════════════════════════════════════

    private static object? InvokeRecordPatches(string name, params object?[] args)
        => Invoke(typeof(RecordPatches), name, args);

    private static object? InvokeEventSubscriberPatches(string name, params object?[] args)
        => Invoke(typeof(EventSubscriberPatches), name, args);

    private static object? Invoke(Type owner, string name, object?[] args)
    {
        var m = owner.GetMethod(name, Priv)
            ?? throw new InvalidOperationException($"test setup: {owner.Name}.{name} not found");
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // the reflection wrapper is not part of the contract
        }
    }

    // ── Fakes standing in for BC types whose members have moved ─────────────────────────

    private sealed class MovedShape
    {
        public MovedShape(int _) { }
        internal MovedShape(string _) { }
        public List<int>? Present { get; set; }
        internal string? Hidden { get; set; }
        public static int StaticPresent => 7;
        public int present;
        public int Take(int n) => n;
        public class Inner { }
    }

    private sealed class RealizedLazyWithoutValue
    {
        public bool IsValueCreated => true;
    }

    private sealed class UnrealizedLazyWithoutValue
    {
        public bool IsValueCreated => false;
    }

    private sealed class RealizedLazy
    {
        public bool Disposed { get; private set; }
        public bool IsValueCreated => true;
        public object Value => new Disposer(() => Disposed = true);

        private sealed class Disposer(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private sealed class ExpressionWithoutGet
    {
        public int Set(int _) => 0;
    }

    private sealed class MetaQueryWithoutDataItems
    {
        public int Id => 1;
    }

    private sealed class MetaQueryWithDataItems
    {
        public IList DataItems { get; } = new List<object> { new(), new() };
    }

    private sealed class AttributeWithoutTargetObjectId
    {
        public string TargetMethodName => "OnAfterInsert";
    }

    private sealed class SubscriberMethodInfo(object? attribute)
    {
        public object? Attribute { get; } = attribute;
    }
}
