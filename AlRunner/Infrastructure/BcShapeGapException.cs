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
using System.Linq;
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
    /// The one method named <paramref name="name"/> that <paramref name="declaring"/> exposes
    /// under <paramref name="flags"/>, or <c>null</c> when BC declares NONE.
    ///
    /// <para>What this is for: <see cref="Type.GetMethod(string, BindingFlags)"/> throws
    /// <see cref="AmbiguousMatchException"/> the moment Microsoft ships a second method of that
    /// name. That is a bare framework exception carrying no member name, and
    /// <c>MethodScopePatches.NavMethodScope_AssertError</c> rethrows only
    /// <see cref="BcShapeGapException"/> — so under an AL <c>asserterror</c> it is ABSORBED and
    /// the asserterror PASSES, on a call real BC performs fine. Enumerating cannot throw, so
    /// every outcome here is the method, <c>null</c>, or a refusal that names the member
    /// (#3069, and #3062 for the same repair inside the permission slice).</para>
    ///
    /// <para><b>Absence still answers <c>null</c>, deliberately.</b> A member that MOVED is the
    /// null-forgiving shape #3051 tracks, and converting it here would change what every call
    /// site does on a build where the member is simply gone. This helper changes exactly one
    /// outcome — ambiguity — and leaves absence to the call site, which is why it can be dropped
    /// into a site that tolerates null and a site that throws on null without reading either
    /// differently. Use <see cref="RequiredMethod"/> where absence must refuse too.</para>
    ///
    /// <para><paramref name="types"/> pins the signature the call site's <c>Invoke</c> argument
    /// array is built for; pass it wherever the types are derivable from something already in
    /// hand, and an added overload is then RESOLVED rather than merely reported. Where it is
    /// null the method must be unique by name, and a second declaration is refused BY NAME.</para>
    ///
    /// <para>Two survivors are refused rather than guessed. With distinct signatures that is a
    /// real overload the call site cannot choose between; with identical ones it is a
    /// <c>new</c>-hidden member, where <see cref="Type.GetMethod(string, BindingFlags)"/> would
    /// silently hand back the most-derived declaration and could drive the wrong one. This is
    /// therefore STRICTER than what it replaces in that one case — measured on Ncl
    /// 27.5.46862.48827 and 28.1.49838.50621, every call site converted to it resolves to
    /// exactly one candidate, so neither refusal fires on a BC build the runner has seen.</para>
    /// </summary>
    public static MethodInfo? FindMethod(
        Type declaring, string name, BindingFlags flags,
        string surface, string member, string detail, Type[]? types = null)
    {
        var candidates = declaring.GetMethods(flags)
            .Where(m => string.Equals(m.Name, name, StringComparison.Ordinal))
            .Where(m => types == null
                        || m.GetParameters().Select(pp => pp.ParameterType).SequenceEqual(types))
            .ToArray();

        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length == 0) return null;

        throw new BcShapeGapException(
            surface, member,
            $"BC declares {candidates.Length} methods named {name}{Signature(types)}"
            + $" on {declaring.Name}, so the runner cannot tell which one its call site means"
            + $" — {detail}");
    }

    /// <summary>
    /// <see cref="FindMethod"/> for a call site that cannot proceed without the method: absence
    /// refuses too, naming what was looked for instead of handing back a null whose
    /// <see cref="NullReferenceException"/> lands somewhere that names nothing.
    /// </summary>
    public static MethodInfo RequiredMethod(
        Type declaring, string name, BindingFlags flags,
        string surface, string member, string detail, Type[]? types = null)
        => FindMethod(declaring, name, flags, surface, member, detail, types)
           ?? throw new BcShapeGapException(
               surface, member,
               $"method not found{Signature(types)} on {declaring.Name} — {detail}");

    private static string Signature(Type[]? types)
        => types == null ? string.Empty : $" with signature ({string.Join(", ", types.Select(t => t.Name))})";

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

    // ── THE NULL-FORGIVING HALF (#3051) ─────────────────────────────────────────────────
    //
    // `t.GetProperty("X")!` is a COMPILER ANNOTATION. It throws nothing. When Microsoft moves
    // X the lookup hands back a silent null and the NullReferenceException lands at the first
    // USE of it — `.PropertyType`, `.GetValue`, `.Invoke` — on a line that no longer names X.
    // MethodScopePatches.NavMethodScope_AssertError is an unfiltered catch(Exception), so on
    // any AL-entered path that NRE is SWALLOWED and `asserterror` PASSES on a read real BC
    // performs fine. That is an inverted result, not merely a hidden gap (#3046 measured it).
    //
    // The helpers below are the one-line replacement. They resolve exactly what the `!` site
    // resolved — same BindingFlags, same overload selection — and raise a BcShapeGapException
    // naming `Declaring.Member` when the read cannot be performed. Nothing else changes:
    // a member that IS there comes back exactly as before.
    //
    // WHAT THEY ARE NOT FOR. The line in this file's header still governs. These say "the read
    // could not be performed", so they belong on BC-internals lookups only. A lookup of the
    // runner's OWN members (`typeof(BcRuntime).GetMethod(nameof(...))`) or of a BCL member
    // (`typeof(string).GetField(nameof(string.Empty))`, `ValueTuple`'s Item1) is not a BC
    // layout question at all and stays as it is — see BcInternalsNullForgivingGuardTests, which
    // holds the remaining population to an exact per-file count.

    /// <summary>Public or non-public, instance — the flags most BC-internals reads want.</summary>
    public const BindingFlags AnyInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>Public or non-public, static.</summary>
    public const BindingFlags AnyStatic =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>
    /// The property <paramref name="name"/> on <paramref name="declaring"/> resolved with
    /// <paramref name="flags"/>, or a <see cref="BcShapeGapException"/> naming it.
    /// </summary>
    public static PropertyInfo RequiredProperty(
        Type declaring, string name, BindingFlags flags, string surface, string? detail = null)
        => declaring.GetProperty(name, flags)
           ?? throw Gap(declaring, name, "property", flags, surface, detail);

    /// <summary>
    /// The method <paramref name="name"/> on <paramref name="declaring"/> resolved with
    /// <paramref name="flags"/>, or a <see cref="BcShapeGapException"/> naming it.
    /// </summary>
    public static MethodInfo RequiredMethod(
        Type declaring, string name, BindingFlags flags, string surface, string? detail = null)
        => declaring.GetMethod(name, flags)
           ?? throw Gap(declaring, name, "method", flags, surface, detail);

    /// <summary>
    /// The method <paramref name="name"/> whose parameter types are exactly
    /// <paramref name="types"/>, or a <see cref="BcShapeGapException"/> naming it. The overload
    /// filter is part of the question: a method that survives a rename but changes its parameter
    /// list is the same "BC's layout moved" case as an absent one.
    /// </summary>
    public static MethodInfo RequiredMethod(
        Type declaring, string name, BindingFlags flags, Type[] types, string surface, string? detail = null)
        => declaring.GetMethod(name, flags, binder: null, types: types, modifiers: null)
           ?? throw Gap(declaring, name, $"method taking ({TypeList(types)})", flags, surface, detail);

    /// <summary>
    /// The field <paramref name="name"/> on <paramref name="declaring"/> resolved with
    /// <paramref name="flags"/>, or a <see cref="BcShapeGapException"/> naming it.
    ///
    /// <para>Deliberately NOT the hierarchy walk <see cref="RequiredField(Type, string, string, string)"/>
    /// does: these sites replace a <c>GetField(name, flags)</c> that never walked either, and
    /// silently widening the search would change which member a converted site resolves.</para>
    /// </summary>
    public static FieldInfo RequiredField(
        Type declaring, string name, BindingFlags flags, string surface, string? detail = null)
        => declaring.GetField(name, flags)
           ?? throw Gap(declaring, name, "field", flags, surface, detail);

    /// <summary>
    /// The constructor of <paramref name="declaring"/> taking exactly <paramref name="types"/>,
    /// or a <see cref="BcShapeGapException"/> naming the signature that was looked for.
    /// </summary>
    public static ConstructorInfo RequiredConstructor(
        Type declaring, BindingFlags flags, Type[] types, string surface, string? detail = null)
        => declaring.GetConstructor(flags, binder: null, types: types, modifiers: null)
           ?? throw new BcShapeGapException(
               surface,
               $"{declaring.Name}..ctor({TypeList(types)})",
               detail ?? $"constructor not found — the runner constructs it to serve {surface}; BC's layout has moved");

    /// <inheritdoc cref="RequiredConstructor(Type, BindingFlags, Type[], string, string?)"/>
    public static ConstructorInfo RequiredConstructor(
        Type declaring, Type[] types, string surface, string? detail = null)
        => RequiredConstructor(declaring, AnyInstance, types, surface, detail);

    /// <summary>
    /// The nested type <paramref name="name"/> on <paramref name="declaring"/>, or a
    /// <see cref="BcShapeGapException"/> naming it.
    /// </summary>
    public static Type RequiredNestedType(
        Type declaring, string name, BindingFlags flags, string surface, string? detail = null)
        => declaring.GetNestedType(name, flags)
           ?? throw Gap(declaring, name, "nested type", flags, surface, detail);

    private static BcShapeGapException Gap(
        Type declaring, string name, string kind, BindingFlags flags, string surface, string? detail)
        => new(surface,
               $"{declaring.Name}.{name}",
               detail ?? $"{kind} not found with {flags} — the runner reads it to serve {surface}; BC's layout has moved");

    private static string TypeList(Type[] types)
        => types.Length == 0 ? "" : string.Join(", ", Array.ConvertAll(types, t => t.Name));
}
