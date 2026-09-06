// TestPageShapeGaps — the sixteen TestPage refusals in MockTestPage.cs and RunnerPageInstance.cs
// that were claiming a permanent scope boundary for a runner gap, and the per-site
// classification behind each of the twenty-five citations in those two files (#2999).
//
// ── WHAT WAS WRONG ───────────────────────────────────────────────────────────────────────
//   #2894 fixed twelve of these in RecordPatches.ObjectMetadataSystemTable.cs, #2945 fixed 55
//   across the virtual-table populators, and #2966/#3001 classified the remaining 77 sites
//   elsewhere under AlRunner/ and corrected 27. It deliberately left these two files alone,
//   because in-flight TestPage work was editing both and a factory sweep would have collided.
//   #2999 is that deferral coming due, and it is deliberately narrow: it lists the sixteen
//   gaps by line AND the citations in the same two files that are genuinely permanent, so the
//   follow-up cannot over-sweep.
//
//   docs/scope.md is the manifest of what is PERMANENTLY out of scope. Citing it for a surface
//   the runner intends to support tells the next developer to stop looking, and it has a
//   RUNTIME consequence, which is why this is a correctness fix and not a wording one.
//   ApplicationObjectBasePatches.IsPermanentOutOfScope:
//
//       return oos != null && !oos.Reason.StartsWith("not-yet-implemented", StringComparison.Ordinal);
//
//   Under a docs/scope.md anchor that returns TRUE, so an AL [TryFunction] traps a runner
//   shape gap into `false` — the silent default .claude/rules/loud-failures.md exists to
//   prevent — and the test goes green having quietly done without the surface.
//
//   TestPage work has a SECOND and sharper seam, and it is the reason two of these sixteen
//   are a different type entirely. `asserterror Foo()` where Foo reaches one of these: on real
//   BC, Foo runs and returns, so the asserterror FAILS ("expected an error"). If the runner
//   absorbs the gap the asserterror PASSES. That does not hide a result, it INVERTS one.
//
// ── THE THREE BUCKETS, AND WHAT LANDED IN EACH ───────────────────────────────────────────
//   The same buckets #2894, #2945 and #2966 used. A refusal belongs in (1) only when BC ITSELF
//   cannot answer, so that an AL [TryFunction] reading `false` is the OBSERVABLE BC OUTCOME
//   rather than a runner gap quietly absorbed.
//
//     (1) genuinely out of scope — keep the refusal and the scope.md link ..........  9
//     (2) in scope, not yet answerable — needs the "not-yet-implemented" anchor .... 14
//     (2b) in scope, implemented, but BC's own internals could not be READ .........  2
//     (3) deliberate divergence — the runner answers on purpose, never throws ......  0
//                                                                                   ----
//                                                                                     25
//
//   NINE KEPT, NOT FOURTEEN. #2999's prose says "the other fourteen citations", but its own
//   enumeration of them lists nine, and nine is what the files hold: 25 citations measured on
//   origin/main (17 in MockTestPage.cs, 8 in RunnerPageInstance.cs) minus the 16 gaps it lists
//   by line. The number used here is the measured one; the issue's list of WHICH sites are
//   permanent is correct and is what this change respects. Those nine citations sit on eight
//   throws, because the option-value refusal spells the pointer on both branches of one
//   ternary.
//
//   NOTHING landed in (3), and that is structural rather than an oversight — #3001 found zero
//   for the same reason. An expect-divergence surface RETURNS AN ANSWER rather than throwing
//   (docs/expectations.md), so by construction a refusal site cannot be one.
//
//   (2b) is #2995's BcShapeGapException, and its rule is narrow: raise it when the read COULD
//   NOT BE PERFORMED, never when the read succeeded and the answer was merely unwelcome. Two
//   of the sixteen meet that bar and the other fourteen do not — see the tables below, where
//   the two SubPageLink siblings that look like they qualify are shown not to.
//
// ── (2), CORRECTED HERE: fourteen sites ──────────────────────────────────────────────────
//
//   MockTestPage.cs — ten. Every one is the runner failing to build or resolve something it
//   owns; real BC has a page object, an owner and a control binding for all of them.
//     GetPart, no part definition ... no AL page object was built for the hosting page, so its
//                                     part definitions are unavailable (or the built page's
//                                     metadata declares no part with this control id — also
//                                     the runner's, since the AL COMPILER resolved the part by
//                                     name before a control id ever reached here).
//     GetPart, no owner ............. the hosting page was built without an ITreeObject owner.
//                                     A runner internal with no BC counterpart to decline.
//     GetPart, not live (x2) ........ the part's own page could not be driven live. TWO throw
//                                     sites with byte-identical reason text, on the recordless
//                                     branch and on the fall-through — one shape that claimed
//                                     itself twice, now built in one place.
//     SubPageLinks, FieldID <= 0 .... the part's own field this link constrains could not be
//                                     resolved. NOT a BC-shape gap: the 0 is written by the
//                                     runner's OWN DependencyPageMetadataXml.EmitSubFormLinkXml
//                                     when it cannot resolve the part field NAME to an id.
//     SubPageLinks, FIELD value ..... a FIELD link's value is not the parent's field number.
//                                     NOT a BC-shape gap either, and for the same reason: that
//                                     same emitter deliberately writes the unresolved field
//                                     NAME here precisely so this refusal fires. The read
//                                     succeeded; the answer is about the RUNNER's metadata
//                                     reconstruction, which is the line #2995 draws.
//     ControlIdToField, unresolved .. bound neither to a source-table field nor to a page
//                                     variable the runner could resolve.
//     TestPageOptionValue.Resolve ... the control is bound to an Option carrying no option
//                                     metadata, so a value cannot be resolved by name.
//     Lookup / Drilldown ............ no AL page object was built for this page, so its
//                                     OnLookup / OnDrillDown trigger cannot be reached.
//
//   RunnerPageInstance.cs — four.
//     EvaluateProperty (x3) ......... a frozen-at-open expression, a live expression, and an
//                                     expression the parser could not evaluate at all. Real BC
//                                     evaluates every AL Boolean property expression; these are
//                                     shapes the runner's own evaluator does not take yet
//                                     (#2596 tracks one of them). The two "not a Boolean" sites
//                                     are again one shape with two throw sites.
//     FindTriggerOnTarget ........... two emitted methods on one object hash to the same member
//                                     id. The collision is in the RUNNER's own name→id hash
//                                     over emitted method names, not in anything BC states.
//
// ── (2b), NOW BcShapeGapException: two sites ─────────────────────────────────────────────
//   Both meet #2995's test — the runner could not perform the read at all:
//
//   MockTestPage.SubPageLinks, unknown FilterType.
//     Reached only when BC's own Microsoft.Dynamics.Nav.Types.Metadata.FilterType holds a
//     member outside FIELD/CONST/FILTER. MEASURED on BC 28.1's Microsoft.Dynamics.Nav.Types.dll
//     (System.Reflection.Metadata dump of the enum's static fields): exactly CONST, FILTER,
//     FIELD. And the runner's own emitter writes only those three spellings, so a fourth value
//     can ONLY have come from BC's compiled metadata. That makes it a property of which BC
//     build is on disk — true on one leg and false on another in the same run — which is
//     exactly the case #2995 says must not be declarable as an expected scope boundary.
//
//   RunnerPageInstance.RaiseSourceFieldOnLookup, undeterminable field trigger.
//     RecordPatches.TryHasFieldLookupTrigger is three-valued ON PURPOSE, and it returns null
//     ONLY when EnsureFieldTriggerReflection could not resolve BC's private
//     NCLMetaField.<EventTriggerDataValue>k__BackingField / EventTriggerData.<LookupHandler>
//     k__BackingField, or the reflection threw. A successful read that says "no trigger"
//     returns false and lands on the PERMANENT refusal below instead. The site's own comment
//     already said "This is a runner/BC-shape problem, not a problem with the AL under test" —
//     it was describing a BcShapeGapException while raising a scope claim.
//
// ── (1), LEFT ALONE: nine citations that really are permanent ────────────────────────────
//   Read and kept, exactly as #2999 requires. Per file, so the next reader does not re-derive:
//
//     MockTestPage.cs (6 citations / 5 throws)
//       * a page with no SourceTable (the StandardDialog shape) — BC has no record-backed
//         rowset for one either, so `false` is BC's own answer;
//       * an OnQueryClosePage veto, which in BC leaves the page open awaiting a user (§3.11);
//       * a control not bound to a source-table field, used to LOCATE A ROW;
//       * three AL-AUTHORING errors real BC also raises — an option value that is neither a
//         member nor a caption (two branches of one ternary, hence 6 citations over 5 throws),
//         and a date spelling TestPage SetValue never produces.
//
//     RunnerPageInstance.cs (3 citations, of which 2 are uncontested and the third is not)
//       * two lookups that come from a TableRelation and would open the related table's list
//         page — §3.11 client interaction, and pinned from AL by
//         tests/runner-extras/testpage-lookup-tablerelation-oos;
//       * an action whose effect is RunObject. #2999 lists this among the permanent ones,
//         but #2931 (PR #2951, in flight) RECLASSIFIES it as not-yet-implemented on the
//         strength of a real-service-tier measurement — corpus PR
//         StefanMaron/BusinessCentral.AL.Language.Tests#172 invoked a RunObject action on a
//         TestPage across all 8 BC legs and it opened nothing and raised nothing. A measured
//         verdict outranks a classification made by reading (see
//         .claude/rules/ask-the-corpus-before-claiming-bc-behavior.md), so that site is #2931's
//         to decide and is UNTOUCHED here. The test for this file allows either outcome for it
//         and pins the two lookups exactly.
//
// ── A SIBLING DEFECT FOUND HERE, FIXED IN #3026 ──────────────────────────────────────────
//   The (2b) site above refuses loudly when _fLookupHandlerBacking / _fValidateHandlerBacking
//   could not be resolved. The WRITE path over the same two fields used to do the opposite:
//   RecordPatches.NclMetaTableBuilder.cs guarded every handler install with
//   `&& _fValidateHandlerBacking != null` (and the lookup equivalent), so on a BC build whose
//   layout moved the field trigger was SILENTLY NEVER INSTALLED and the AL that depends on it
//   ran with no trigger at all — and WireFieldTriggerHandlers still returned true. Two code
//   paths over one piece of state, one refusing and one defaulting. Filed as #3026 rather than
//   folded in, because it changes trigger INSTALLATION rather than refusal wording and needed
//   its own RED → GREEN; that fix is now in AlRunner/Patches/FieldTriggerShapeGaps.cs. Still
//   not reachable on 27.0–28.4, where every member resolves.
//
// ── WHY THE DOC LINK MOVED ───────────────────────────────────────────────────────────────
//   docs/limitations.md#testpage-shape-gaps, not docs/scope.md. scope.md documents permanent
//   boundaries and has nothing to say about any of these; sending a reader there asserted a
//   permanence that is not true AND gave them no section to read. Several of these sites also
//   rendered the pointer twice ("… See docs/scope.md — see docs/scope.md", #2766/#2931),
//   because they spelled it in the reason while BuildMessage appends its own. Passing the link
//   as the docAnchor argument instead removes that as a consequence rather than as a sweep.
//
//   No classification moves as a result. ExpectationManifest.ReasonAnchor cuts at the first
//   em-dash, so the anchor these report goes from e.g. "testpage-lookup" to
//   "not-yet-implemented"; no manifest entry matches on any of them. The four entries in
//   tests/expectations/known-gaps-testpage-control-property.json match on
//   (CodeunitName, Method) under Mode=expect-fail-known-gap, which means "must fail" — and a
//   not-yet-implemented refusal still fails. The AL suite that DOES assert on one of these
//   anchors, tests/runner-extras/testpage-lookup-tablerelation-oos, drives a PERMANENT site
//   that is untouched.

using AlRunner.Infrastructure;

namespace AlRunner.Patches;

/// <summary>
/// One factory per corrected TestPage surface, so a refusal cannot drift back to claiming a
/// permanent scope boundary one call site at a time. See this file's header for the per-site
/// classification of all thirty citations in <c>MockTestPage.cs</c> and
/// <c>RunnerPageInstance.cs</c>, <see cref="RunnerShapeGap"/> for the same shape applied
/// outside the TestPage surface (#2966), and
/// <see cref="AlRunner.Infrastructure.BcShapeGapException"/> for the two sites here that are
/// not scope claims at all.
/// </summary>
internal static class TestPageShapeGap
{
    /// <summary>Where a reader should look for these gaps. NOT docs/scope.md.</summary>
    internal const string Doc = "docs/limitations.md#testpage-shape-gaps";

    /// <summary>
    /// The one place these refusals are built. The surface's own anchor is kept as the second
    /// token of the reason so the surfaces stay distinguishable to a reader and to any future
    /// expectations entry, exactly as <see cref="RunnerShapeGap"/> and
    /// <c>RecordPatches.VirtualTableShapeGap</c> do.
    /// </summary>
    /// <param name="api">The AL-visible surface the test touched. Must not contain " — ":
    /// OutOfScopeMessage.TryParse cuts the api from the reason at the first one (#2945).</param>
    /// <param name="surface">The surface's own anchor, e.g. "testpage-part".</param>
    /// <param name="detail">What was actually missing.</param>
    private static RunnerOutOfScopeException Build(string api, string surface, string detail)
        => new(api, $"not-yet-implemented — {surface}: {detail}", Doc);

    /// <summary>A subpage part the runner could not resolve, own, or drive live.</summary>
    internal static RunnerOutOfScopeException Part(string api, string detail)
        => Build(api, "testpage-part", detail);

    /// <summary>A SubPageLink entry whose fields the runner's own metadata could not resolve.</summary>
    internal static RunnerOutOfScopeException PartLink(string api, string detail)
        => Build(api, "testpage-part-link", detail);

    /// <summary>A control the runner could not bind to a source-table field or a page variable.</summary>
    internal static RunnerOutOfScopeException ControlBinding(string api, string detail)
        => Build(api, "testpage-control-binding", detail);

    /// <summary>A Visible/Editable/Enabled expression the runner's evaluator could not answer.</summary>
    internal static RunnerOutOfScopeException ControlProperty(string api, string detail)
        => Build(api, "testpage-control-property", detail);

    /// <summary>An Option-bound control the runner could not resolve a value by name for.</summary>
    internal static RunnerOutOfScopeException OptionValue(string api, string detail)
        => Build(api, "testpage-option-value", detail);

    /// <summary>An OnLookup trigger the runner could not reach.</summary>
    internal static RunnerOutOfScopeException Lookup(string api, string detail)
        => Build(api, "testpage-lookup", detail);

    /// <summary>An OnDrillDown trigger the runner could not reach.</summary>
    internal static RunnerOutOfScopeException DrillDown(string api, string detail)
        => Build(api, "testpage-drilldown", detail);

    /// <summary>
    /// Two emitted methods on one object resolving to a single member id. The surface is passed
    /// in because the caller serves OnValidate, OnAction, OnLookup and OnDrillDown from one
    /// method and the anchor names which one was being resolved.
    /// </summary>
    internal static RunnerOutOfScopeException TriggerAmbiguity(string api, string surface, string detail)
        => Build(api, surface, detail);
}
