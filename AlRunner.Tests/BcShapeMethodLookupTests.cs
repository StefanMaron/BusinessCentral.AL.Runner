// BcShapeMethodLookupTests — name-only BC method lookups outside the permission slice (#3069).
//
// ── THE MECHANISM, AND WHAT IT IS *NOT* ──────────────────────────────────────────────────
// `Type.GetMethod(name)` / `Type.GetMethod(name, flags)` do NOT silently hand back the wrong
// member when Microsoft ships a second method of that name. Measured, not assumed — the first
// arm below asserts it: reflection's default binder raises AmbiguousMatchException. So the
// defect is not a wrong answer at the reflection layer.
//
// It becomes a wrong answer one layer up. MethodScopePatches.NavMethodScope_AssertError — AL's
// `asserterror` seam — rethrows exactly one type (BcShapeGapException) and absorbs everything
// else. An AmbiguousMatchException raised under an `asserterror` is therefore SWALLOWED, and the
// asserterror PASSES on a call real BC performs fine. Green, and the opposite of BC's answer.
// #3062 fixed that inversion for the three permission-slice files; this is the same repair for
// the record/query paths, behind one shared helper instead of a file-private one.
//
// No `!` appears in the shape, so #3051's sweep of null-forgiving lookups cannot find it: these
// two issues share a SEAM, not a mechanism. #3051 is a member that MOVED (silent null, NRE at
// the first use); this is a member that MULTIPLIED (a throw of the wrong type). BcShape.FindMethod
// deliberately still answers null on absence, so converting a site here does not change what it
// does on a build where the member is gone — that half stays #3051's.
//
// ── WHY IT IS LATENT TODAY, AND WHY IT IS STILL REAL ─────────────────────────────────────
// Measured over Ncl 27.5.46862.48827 and 28.1.49838.50621: every BC method name the converted
// call sites look up has exactly ONE declaration on the type they look it up on, so the
// conversion changes nothing on any BC build the runner has seen.
//
// Microsoft does add overloads to types the runner reflects on, though — measured on the same
// two builds, Microsoft.Dynamics.Nav.Runtime.NavReport.Execute went from 4 declarations to 6
// (`Execute(String, String)` and `Execute(String, String, NavRecordRef)` are new in 28.1). The
// runner does not look that one up by name; the point is that the event is ordinary, and the day
// it lands on a name the runner DOES look up, this is the difference between a named refusal and
// a green asserterror.
//
// ── MEASURED RED (production call sites unconverted, this file unchanged) ────────────────
//   AssertError_TearsThrough_InsteadOfSwallowingTheAmbiguousEvaluate
//       Assert.Throws() Failure: No exception was thrown
//   TryFunction_CannotTrapTheRefusal_OnTheCorrectedEvaluatePath
//       System.Reflection.AmbiguousMatchException : Ambiguous match found.
//   BcShape_FindMethod_* / NoBcTypedNameOnlyMethodLookup_* : green either way (helper + scan)
//
// The first arm IS the inversion: the seam swallowed the AmbiguousMatchException and returned
// normally, which in AL is an `asserterror` that passed. The second shows the other seam already
// refused it, so only asserterror inverts — that arm is a CONTROL, not a second RED.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(RecordPatchesSerialCollection.Name)]
public sealed class BcShapeMethodLookupTests
{
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    // ══ 1. The premise, measured rather than assumed ════════════════════════════════════

    [Fact]
    public void TypeGetMethodByName_ThrowsAmbiguousMatch_WhenBcShipsASecondDeclaration()
    {
        Assert.Throws<AmbiguousMatchException>(
            () => typeof(TwoEvaluates).GetMethod("Evaluate", PublicInstance));

        // ... and the single-declaration type it stands in for does not, so the throw above is
        // about the second declaration and not about the lookup itself.
        Assert.NotNull(typeof(OneEvaluate).GetMethod("Evaluate", PublicInstance));
    }

    /// <summary>
    /// The other half of the premise, and the reason this issue is NOT "reflection returns the
    /// wrong member": a `new`-hidden declaration whose SIGNATURE changed is ambiguous too, so
    /// reflection refuses rather than picking. There is no silent-wrong-member outcome to fix
    /// at this layer — the wrong answer is manufactured by the seam that absorbs the throw.
    /// </summary>
    [Fact]
    public void TypeGetMethodByName_ThrowsAmbiguousMatch_ForAHiddenDeclarationWithANewSignature()
    {
        Assert.Throws<AmbiguousMatchException>(
            () => typeof(HidesEvaluateWithADifferentSignature).GetMethod("Evaluate", PublicInstance));
    }

    /// <summary>
    /// The seam, on the raw expression the production sites used to contain: it returns NORMALLY.
    /// In AL that is `asserterror` passing over a call real BC performs fine. This arm asserts
    /// the FRAMEWORK behaviour the fix routes around, so it stays green after the fix.
    /// </summary>
    [Fact]
    public void AssertError_AbsorbsARawAmbiguousMatch_WhichIsWhatInvertsTheResult()
    {
        var reached = false;

        BcRuntime.NavMethodScope_AssertError(null!, () =>
        {
            reached = true;
            typeof(TwoEvaluates).GetMethod("Evaluate", PublicInstance);
        });

        Assert.True(reached, "the body must actually run, else this arm proves nothing");
    }

    // ══ 2. THE INVERSION, on the real call path ═════════════════════════════════════════
    //
    // RecordPatches.EvaluateFilterExpression is the runner's driver for BC's
    // FilterExpression.Evaluate(NavValue, ISortingRulesProvider) — the WHERE evaluation behind AL
    // query filtering, so it is reachable from AL and therefore from `asserterror`. Both arms
    // drive PRODUCTION code with the two reflection statics it reads injected for one call;
    // nothing here re-implements the helper.

    [Fact]
    public void AssertError_TearsThrough_InsteadOfSwallowingTheAmbiguousEvaluate()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => EvaluateWith(typeof(TwoEvaluates), new TwoEvaluates())));

        Assert.Equal("FilterExpression.Evaluate", ex.Member);
        Assert.Contains("BC declares 2 methods named Evaluate", ex.Detail, StringComparison.Ordinal);
        Assert.Contains("cannot tell which one", ex.Detail, StringComparison.Ordinal);
    }

    // CONTROL, not a second RED: NavApplicationObjectBase_TryInvoke rethrew the raw
    // AmbiguousMatchException before this change too. What it pins is that the corrected refusal
    // still tears through, rather than becoming something an AL [TryFunction] can trap.
    [Fact]
    public void TryFunction_CannotTrapTheRefusal_OnTheCorrectedEvaluatePath()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => EvaluateWith(typeof(TwoEvaluates), new TwoEvaluates())));

        Assert.Equal("FilterExpression.Evaluate", ex.Member);
    }

    /// <summary>
    /// CONTROL, and the arm that makes the two above mean something: with a SINGLE Evaluate the
    /// same production path resolves it and returns the fake's own answer. Both directions, so a
    /// helper that always refused — or one that always answered a constant — fails here.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EvaluateFilterExpression_StillDrivesTheSingleEvaluate_AndReturnsItsAnswer(bool answer)
    {
        Assert.Equal(answer, EvaluateWith(typeof(OneEvaluate), new OneEvaluate(answer)));
    }

    /// <summary>
    /// Absence still refuses the way it did before — with the call site's own
    /// InvalidOperationException, NOT a shape gap. This pins that the conversion moved exactly one
    /// outcome (ambiguity) and left the null-forgiving half to #3051, so the two issues stay
    /// separately fixable.
    /// </summary>
    [Fact]
    public void EvaluateFilterExpression_KeepsItsOwnRefusal_WhenEvaluateIsGoneEntirely()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EvaluateWith(typeof(NoEvaluate), new NoEvaluate()));

        Assert.Contains("FilterExpression.Evaluate", ex.Message, StringComparison.Ordinal);
        Assert.IsNotType<BcShapeGapException>(ex);
    }

    // ══ 3. BcShape.FindMethod / RequiredMethod ══════════════════════════════════════════

    [Fact]
    public void FindMethod_ResolvesTheOnlyDeclaration()
    {
        var m = BcShape.FindMethod(typeof(OneEvaluate), "Evaluate", PublicInstance,
            "surface", "OneEvaluate.Evaluate", "detail");

        Assert.NotNull(m);
        Assert.Equal(2, m!.GetParameters().Length);
    }

    [Fact]
    public void FindMethod_AnswersNull_WhenBcDeclaresNone_SoAbsenceStaysTheCallSitesProblem()
    {
        Assert.Null(BcShape.FindMethod(typeof(NoEvaluate), "Evaluate", PublicInstance,
            "surface", "NoEvaluate.Evaluate", "detail"));
    }

    [Fact]
    public void FindMethod_RaisesAShapeGapNamingTheMemberAndTheCount_WhenBcDeclaresTwo()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcShape.FindMethod(
            typeof(TwoEvaluates), "Evaluate", PublicInstance,
            "Query filtering (WHERE evaluation)", "FilterExpression.Evaluate", "the filter cannot be applied"));

        Assert.Equal("Query filtering (WHERE evaluation)", ex.Surface);
        Assert.Equal("FilterExpression.Evaluate", ex.Member);
        Assert.Contains("BC declares 2 methods named Evaluate on TwoEvaluates", ex.Detail,
            StringComparison.Ordinal);
        Assert.Contains("the filter cannot be applied", ex.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// STRICTER than what it replaces, on purpose and in exactly one case: a `new`-hidden
    /// declaration of the SAME signature. Type.GetMethod hands back the most-derived one without
    /// complaint (asserted here, so the difference is measured), which could silently drive the
    /// wrong declaration; this refuses instead.
    /// </summary>
    [Fact]
    public void FindMethod_RefusesANewHiddenDeclarationOfTheSameSignature_WhichGetMethodWouldHaveGuessed()
    {
        var guessed = typeof(HidesEvaluateWithTheSameSignature).GetMethod("Evaluate", PublicInstance);
        Assert.Equal(typeof(HidesEvaluateWithTheSameSignature), guessed!.DeclaringType);

        var ex = Assert.Throws<BcShapeGapException>(() => BcShape.FindMethod(
            typeof(HidesEvaluateWithTheSameSignature), "Evaluate", PublicInstance,
            "surface", "Hidden.Evaluate", "detail"));

        Assert.Contains("BC declares 2 methods named Evaluate", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FindMethod_ResolvesTheAskedForOverload_WhenTheSignatureIsPinned()
    {
        var two = BcShape.FindMethod(typeof(TwoEvaluates), "Evaluate", PublicInstance,
            "surface", "member", "detail", new[] { typeof(object), typeof(object) });

        Assert.Equal(2, two!.GetParameters().Length);
    }

    // The pinned selection DISCRIMINATES: asked for the one-parameter overload it returns THAT
    // one, so the arm above is not satisfied by a helper that hands back the first candidate.
    [Fact]
    public void FindMethod_ReturnsTheOtherOverload_WhenThatIsTheSignatureAskedFor()
    {
        var one = BcShape.FindMethod(typeof(TwoEvaluates), "Evaluate", PublicInstance,
            "surface", "member", "detail", new[] { typeof(object) });

        Assert.Equal(1, one!.GetParameters().Length);
        Assert.Equal(typeof(object), one.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void RequiredMethod_RaisesAShapeGapNamingTheSignature_WhenTheParametersHaveMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcShape.RequiredMethod(
            typeof(TwoEvaluates), "Evaluate", PublicInstance,
            "surface", "TwoEvaluates.Evaluate", "the filter cannot be applied",
            new[] { typeof(int) }));

        Assert.Equal("TwoEvaluates.Evaluate", ex.Member);
        Assert.Contains("method not found with signature (Int32)", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredMethod_ResolvesWhenItIsThere_SoTheRefusalIsAboutAbsence()
    {
        Assert.Equal("Evaluate", BcShape.RequiredMethod(typeof(OneEvaluate), "Evaluate",
            PublicInstance, "surface", "member", "detail").Name);
    }

    [Fact]
    public void FindMethod_FindsANonPublicMethod_SoTheConvertedSitesKeepTheirOwnFlags()
    {
        var m = BcShape.FindMethod(typeof(OneEvaluate), "HiddenHelper",
            BindingFlags.NonPublic | BindingFlags.Instance, "surface", "member", "detail");

        Assert.NotNull(m);
        Assert.Equal("HiddenHelper", m!.Name);
    }

    // ══ 4. Plumbing ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drives production <c>RecordPatches.EvaluateFilterExpression</c> with the two reflection
    /// statics it reads pointed at <paramref name="filterExpressionType"/> for exactly one call,
    /// and restores both in a finally. Nothing in production becomes settable for the test.
    /// </summary>
    private static bool EvaluateWith(Type filterExpressionType, object expr) => WithStatics(
        new (string, object?)[] { ("_tFilterExpr", filterExpressionType), ("_mFilterExprEvaluate", null) },
        () => (bool)Invoke("EvaluateFilterExpression", expr, new object(), null)!);

    private static T WithStatics<T>((string Name, object? Value)[] statics, Func<T> body)
    {
        var fields = statics.Select(x => (Field: Static(x.Name), x.Value)).ToArray();
        var saved = fields.Select(f => f.Field.GetValue(null)).ToArray();
        try
        {
            foreach (var (field, value) in fields) field.SetValue(null, value);
            return body();
        }
        finally
        {
            for (var i = 0; i < fields.Length; i++) fields[i].Field.SetValue(null, saved[i]);
        }
    }

    private static FieldInfo Static(string name)
        => typeof(RecordPatches).GetField(name, Priv)
           ?? throw new InvalidOperationException($"test setup: RecordPatches.{name} not found");

    private static object? Invoke(string name, params object?[] args)
    {
        var m = typeof(RecordPatches).GetMethod(name, Priv)
            ?? throw new InvalidOperationException($"test setup: RecordPatches.{name} not found");
        try
        {
            return m.Invoke(null, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // the reflection wrapper is not part of the contract
        }
    }

    /// <summary>The shape BC declares today: exactly one Evaluate.</summary>
    public sealed class OneEvaluate
    {
        private readonly bool _answer;
        public OneEvaluate(bool answer = true) => _answer = answer;
        public bool Evaluate(object navValue, object? sortingRules) => _answer;
        internal void HiddenHelper() { }
    }

    /// <summary>Stands in for FilterExpression after Microsoft ships a second Evaluate.</summary>
    public sealed class TwoEvaluates
    {
        public bool Evaluate(object navValue, object? sortingRules) => true;
        public bool Evaluate(object navValue) => true;
    }

    /// <summary>Stands in for a build where Evaluate is gone entirely.</summary>
    public sealed class NoEvaluate
    {
    }

    public class EvaluateBase
    {
        public bool Evaluate(object navValue, object? sortingRules) => false;
    }

    public sealed class HidesEvaluateWithADifferentSignature : EvaluateBase
    {
        public new bool Evaluate(object navValue) => true;
    }

    public sealed class HidesEvaluateWithTheSameSignature : EvaluateBase
    {
        public new bool Evaluate(object navValue, object? sortingRules) => true;
    }
}
