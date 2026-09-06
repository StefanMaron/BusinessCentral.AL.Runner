// BcShapeGapException — the runner could not READ BC's own internals here (#2946).
//
// ── THE THIRD CASE ───────────────────────────────────────────────────────────────────────
// Before this type the runner had two ways to say "AL touched something I cannot do", and
// both of them are claims about SCOPE:
//
//   RunnerOutOfScopeException, reason = a docs/scope.md anchor
//       PERMANENTLY out of scope. SMTP, HTTP egress, printing. BC itself, in an environment
//       that also lacks the surface, raises a trappable error — so an AL [TryFunction]
//       reading `false` reproduces the observable BC outcome. Trapping it is FAITHFUL.
//
//   RunnerOutOfScopeException, reason = "not-yet-implemented — ..."
//       IN scope, not built yet. BC answers on a service tier and the runner does not, so
//       trapping it would turn a runner gap into a green test that lies. It tears through.
//
// Neither fits the thing most reflection guards under AlRunner/Patches/ actually mean: the
// surface is in scope AND implemented, but BC's own internals are not the shape the code
// reflecting on them was written against. A private field that moved, a static that was
// renamed, a member whose value is a type the runner cannot interpret. That is a BUG REPORT
// ABOUT THE RUNNER against a specific BC build — not a statement about scope at all.
//
// ── WHERE IT APPLIES, AND WHERE IT DOES NOT ──────────────────────────────────────────────
// The line is whether the runner OBTAINED the information:
//
//   RAISE THIS       the read could not be performed. The type/field/property is absent, or
//                    it is present and holds something of a shape the runner cannot use.
//                    "TempTableDataProvider.primaryTree not found."
//                    "primaryTree holds a Dictionary`2, which cannot be enumerated."
//
//   DO NOT RAISE IT  the read SUCCEEDED and the answer was merely unwelcome. BC's list came
//                    back empty; a metatable genuinely has no field 3; a skeleton singleton
//                    the RUNNER populates is null; the runner's own store wiring handed no
//                    provider over. Those are answers, and they are answers about the
//                    runner's or the artifact's state, not about BC's layout. They stay
//                    "not-yet-implemented" — see RecordPatches.VirtualTableShapeGap.cs and
//                    RecordPatches.ObjectMetadataSystemTable.cs for the per-site tables.
//
// ── WHY A NEW TYPE RATHER THAN A NEW REASON ANCHOR ───────────────────────────────────────
// The obvious cheaper move is a third anchor on RunnerOutOfScopeException. It was rejected
// for two independent reasons, and the first one is a defect that has already shipped:
//
//   1. Anchor classification is a STRING PREFIX test, and prefixes get mis-spelled silently.
//      ApplicationObjectBasePatches.IsPermanentOutOfScope decides whether an AL [TryFunction]
//      may swallow a refusal with
//          !oos.Reason.StartsWith("not-yet-implemented", StringComparison.Ordinal)
//      and RecordPatches.QueryProjection.cs's "query-join-synthesized-subquery-not-implemented"
//      says not-implemented in words while not STARTING with the token — so a [TryFunction]
//      swallows it today (measured, #2966). A second prefix would be a second chance to make
//      that mistake. A type is checked by the compiler and by `is`, not by spelling.
//
//   2. Anything carrying a RunnerOutOfScopeException can be ABSORBED BY AN `expect-oos`
//      MANIFEST ENTRY. OutOfScopeMessage.FromException finds the typed exception anywhere in
//      the inner chain, hands it to ExpectationClassifier, and an entry whose Reason anchor
//      matches turns the failure into PassOos. A BC-layout regression must never be
//      declarable as an expected out-of-scope surface: it is not a property of the runner at
//      all, it is a property of which BC build is on disk, so it can be true on the BC 28.4
//      leg and false on 27.5 in the same run. Issue #2946 named this as the argument FOR
//      MissingFieldException, which cannot be absorbed — and the argument against
//      MissingFieldException was that it carries no Api/Reason pair to bucket on. This type
//      takes both halves: unabsorbable AND structured.
//
// RunnerOutOfScopeException is `sealed`, so this is deliberately NOT a subclass of it — and
// unsealing it would immediately hand back defect (2), since FromException walks for the base
// type. It derives from System.Exception directly.
//
// ── WHAT AL CAN DO WITH IT: NOTHING ──────────────────────────────────────────────────────
// It tears through BOTH of AL's error-trapping seams, which is what makes it different from
// either RunnerOutOfScopeException flavour:
//
//                              [TryFunction]        asserterror
//   permanent OOS              traps -> false       catches -> passes
//   not-yet-implemented        tears through        catches -> passes   (#2871 tracks this)
//   BC shape gap (this type)   tears through        tears through
//
// The asserterror column is derived, not copied. `asserterror Foo()` where Foo hits a shape
// gap: on real BC, Foo runs and returns, so the asserterror FAILS ("expected an error"). If
// the runner catches the gap, the asserterror PASSES — the opposite of BC's answer, and
// green. Swallowing it does not merely hide a gap, it inverts a result. Nothing relied on
// the old behaviour for this type because the type is new, so this is the contract from the
// start rather than a change to an existing one; #2871 remains open for the middle row,
// which IS an existing contract and a maintainer decision.
//
// ── CLASSIFICATION ───────────────────────────────────────────────────────────────────────
// ExpectationClassifier may NOT absorb one into `expect-oos` (that mode declares a permanent
// scope boundary) nor into `expect-divergence` (that mode declares an answer the runner gives
// on purpose). Both refuse it explicitly, naming the type, instead of falling through to a
// message that would tell the author to go and raise RunnerOutOfScopeException instead.
// `expect-fail-known-gap` still absorbs it, and that is correct: that mode means "must fail,
// and this open issue tracks the work", which is exactly what a shape gap is once someone has
// written it down.
//
// See also:
//   .claude/rules/loud-failures.md         — no silent out-of-scope failures
//   .claude/rules/precompiled-dll-respect.md — why the runner reflects on BC internals at all
//   docs/limitations.md#bc-shape-gaps      — the reader-facing write-up

using System;
using System.Collections;
using System.Reflection;

namespace AlRunner.Infrastructure;

/// <summary>
/// Raised when the runner cannot READ a BC internal it reflects on — the member is absent,
/// or present and holding something the runner cannot interpret. See this file's header for
/// the line between this and <see cref="RunnerOutOfScopeException"/>, and for why it is a
/// separate type rather than a third reason anchor.
/// </summary>
public sealed class BcShapeGapException : Exception
{
    /// <summary>
    /// Message prefix marking a shape gap. Deliberately NOT
    /// <see cref="OutOfScopeMessage.Prefix"/>: <c>OutOfScopeMessage.TryParse</c> matches that
    /// prefix ANYWHERE in a text blob, so a shape-gap message containing it would be recovered
    /// as an out-of-scope signal by the reporter and by the expectations manifest — the exact
    /// absorption this type exists to prevent.
    /// </summary>
    public const string Prefix = "bc-shape-gap: ";

    /// <summary>Where a reader should look. Not <c>docs/scope.md</c>: a shape gap is not a scope claim.</summary>
    public const string DefaultDoc = "docs/limitations.md#bc-shape-gaps";

    /// <summary>The AL-visible surface that was being served, e.g. "Object Metadata (system table 2000000071)".</summary>
    public string Surface { get; }

    /// <summary>The BC internal that could not be read, e.g. "TempTableDataProvider.primaryTree".</summary>
    public string Member { get; }

    /// <summary>What went wrong and what it costs — the actionable half.</summary>
    public string Detail { get; }

    /// <summary>Doc target rendered into the message.</summary>
    public string DocLink { get; }

    public BcShapeGapException(string surface, string member, string detail, string? docLink = null)
        : base(BuildMessage(surface, member, detail, docLink ?? DefaultDoc))
    {
        Surface = surface;
        Member = member;
        Detail = detail;
        DocLink = docLink ?? DefaultDoc;
    }

    // Stable contract format, same em-dash separators as the out-of-scope convention so a
    // reader parses both the same way:
    //     bc-shape-gap: <surface> — <member>: <detail> — see <doc>
    private static string BuildMessage(string surface, string member, string detail, string docLink)
        => $"{Prefix}{surface} — {member}: {detail} — see {docLink}";

    /// <summary>
    /// The shape gap anywhere in <paramref name="ex"/>'s inner-exception chain, else null.
    ///
    /// <para>A chain walk, not an <c>is</c> test, and the difference is load-bearing at the
    /// two trap sites. A refusal raised behind <see cref="MethodBase.Invoke(object, object[])"/>
    /// arrives wrapped in a <see cref="TargetInvocationException"/>, and BC's own
    /// <c>RemapToALExceptionAndThrow</c> can rewrap it as a NavBaseException — which
    /// <c>NavApplicationObjectBase_TryInvoke</c> swallows on its FIRST clause. So both traps
    /// ask this question before they ask any other one.</para>
    /// </summary>
    public static BcShapeGapException? Find(Exception? ex)
    {
        const int MaxDepth = 16;   // guard against self-referential inner chains
        var e = ex;
        for (var d = 0; e != null && d < MaxDepth; d++, e = e.InnerException)
        {
            if (e is BcShapeGapException gap) return gap;
            if (e is AggregateException agg)
                foreach (var inner in agg.InnerExceptions)
                    if (inner is not null && !ReferenceEquals(inner, e) && Find(inner) is { } nested)
                        return nested;
        }
        return null;
    }
}

/// <summary>
/// Reflection reads of BC internals that must not answer a default. Keeps the throw sites
/// short and, more to the point, keeps them ONE type — before #2946 four readers of the same
/// private <c>TempTableDataProvider</c> structure raised three different exception types
/// between them, so what a caller could catch depended on which reader it happened to reach.
/// </summary>
internal static class BcShape
{
    /// <summary>
    /// The private instance field <paramref name="member"/> declared on <paramref name="type"/>
    /// or any base, or a <see cref="BcShapeGapException"/> naming it.
    ///
    /// <para>Resolution goes through <see cref="PrivateMemberLookup"/>, never a plain
    /// <c>GetField(NonPublic)</c>: that does not return a BASE class's private field, and BC's
    /// own <c>CrmTableConnection.CrmTestDataProvider</c> — the provider behind the
    /// <c>'@@test@@'</c> CRM test connection — derives from <c>TempTableDataProvider</c>
    /// (#2725). Reading a derived provider's inherited field as "absent" would turn a
    /// perfectly readable store into a hard failure now that absence refuses.</para>
    /// </summary>
    public static FieldInfo RequiredField(Type type, string member, string surface, string detail)
        => PrivateMemberLookup.Field(type, member)
           ?? throw new BcShapeGapException(surface, $"{type.Name}.{member}", $"field not found — {detail}");

    /// <summary>
    /// <paramref name="value"/> as an <see cref="IEnumerable"/>, or a
    /// <see cref="BcShapeGapException"/> naming what it actually held. A member that exists
    /// but holds an uninterpretable shape is the same "BC's layout moved" case as an absent
    /// one, and folding it into the absent/null branch is how #2786's silent skip happened.
    /// </summary>
    public static IEnumerable RequiredEnumerable(object value, string member, string surface, string detail)
        => value as IEnumerable
           ?? throw new BcShapeGapException(
               surface, member, $"holds a {value.GetType().Name}, which cannot be enumerated — {detail}");
}
