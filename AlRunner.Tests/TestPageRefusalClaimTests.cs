// TestPageRefusalClaimTests — what the sixteen corrected TestPage refusals claim, what an AL
// [TryFunction] does with one, and which nine in the same two files must keep claiming
// permanence (#2999).
//
// WHY THIS IS A RUNNER-SIDE MECHANISM TEST AND NOT AN AL BUNDLE
// ------------------------------------------------------------
// Every one of the sixteen fires when something the RUNNER owns is absent or the wrong shape:
// no AL page object was built for a page, a page built without an ITreeObject owner, a control
// the runner could not resolve to a binding, a SubPageLink field its own DependencyPageMetadataXml
// could not resolve to an id, an Option binding carrying no option metadata, two emitted methods
// hashing to one member id, and — for the two BcShapeGapException sites — a private BC field
// reflection could not read. No AL statement can arrange any of those, so no bundle under
// tests/runner-extras/ can drive one. The subject is the C# refusal contract, which
// .claude/rules/bc-behavior-tests-go-upstream.md classifies as runner-specific (same shape as
// RunnerShapeGapClaimTests, VirtualTableRefusalClaimTests and BcShapeGapConventionTests).
//
// WHAT WAS WRONG
// --------------
// All sixteen led with a testpage-* anchor under docs/scope.md, the manifest of what is
// PERMANENTLY out of scope. The claim is load-bearing, not decorative —
// ApplicationObjectBasePatches.IsPermanentOutOfScope:
//
//     return oos != null && !oos.Reason.StartsWith("not-yet-implemented", StringComparison.Ordinal);
//
// so an AL [TryFunction] trapped a runner gap into `false` and the test went green having
// quietly done without the surface — the silent default loud-failures.md exists to prevent.
//
// The sharper seam is the one TestPage work actually hits: `asserterror Foo()` where Foo
// reaches one of these. On real BC, Foo runs and returns, so the asserterror FAILS. A runner
// that absorbs the gap makes it PASS. That does not hide a result, it INVERTS one — which is
// why the two sites where the runner could not READ BC's internals raise
// BcShapeGapException, whose contract is to tear through both seams (#2946/#2995).
//
// THE CONTROL ARMS — three of them
// --------------------------------
// Without these the suite could pass by discriminating on exception TYPE rather than on what
// the reason claims, which would prove nothing:
//
//   * PermanentTestPageRefusal_IsStillTrappedByATryFunction — the SAME type as the fourteen
//     corrected sites, on a genuinely permanent surface drawn from THIS issue's own kept list,
//     still absorbed into `false`.
//   * PermanentTestPageRefusal_IsStillCatchableByAssertError — the same, on AL's other seam,
//     so the BcShapeGapException tear-through cannot pass by "anything unusual tears through".
//   * KeptRefusals_StillClaimPermanence — the sites #2999 deliberately excluded were read and
//     LEFT ALONE. If a later sweep deletes their citation the classification was wrong, and
//     this fails rather than silently widening the change. NINE citations, not the fourteen
//     #2999's prose claims: 25 measured on origin/main minus the 16 gaps it lists by line, and
//     the issue's own enumeration of the permanent ones lists nine. WHICH sites are permanent
//     is what matters and the issue has that right; only its total is off.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;
using AlRunner;
using AlRunner.Infrastructure;
using AlRunner.Patches;

namespace AlRunner.Tests;

public sealed class TestPageRefusalClaimTests
{
    private const string Doc = "docs/limitations.md#testpage-shape-gaps";
    private const string Detail = "the probe detail";

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string PathOf(string file) => Path.Combine(RepoRoot, "AlRunner", "Patches", file);

    /// <summary>File contents with comment lines stripped — the file headers quote the OLD
    /// wording on purpose, and the claim under test is about code.</summary>
    private static string CodeOf(string file)
    {
        var path = PathOf(file);
        Assert.True(File.Exists(path), $"{file} not found — was it renamed?");
        return string.Join('\n', File.ReadAllLines(path)
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    /// <summary>
    /// <see cref="CodeOf"/> with adjacent string literals joined, so a refusal written as five
    /// concatenated source lines can be matched on the sentence it actually renders. Without
    /// this every marker below would have to encode the exact line breaks of the source, which
    /// makes the test fail on a reflow rather than on the claim it is about.
    /// </summary>
    private static string FlattenedCodeOf(string file)
        => Regex.Replace(CodeOf(file), "\"\\s*\\+\\s*\\$?\"", string.Empty);

    // ══ The fourteen sites that keep RunnerOutOfScopeException, with a corrected anchor ═══

    /// <summary>
    /// One entry per corrected call site, reproducing the arguments the real site passes, so a
    /// factory whose api or anchor is quietly changed fails here rather than in the field.
    /// </summary>
    private static readonly Dictionary<string, (Func<RunnerOutOfScopeException> Build, string Api, string Surface)> Sites =
        new()
        {
            // MockTestPage.cs — ten
            ["part-no-definition"] = (
                () => TestPageShapeGap.Part("TestPage part 42 (page 60100)", Detail),
                "TestPage part 42 (page 60100)", "testpage-part"),
            ["part-no-owner"] = (
                () => TestPageShapeGap.Part("TestPage part 42 → page 60101", Detail),
                "TestPage part 42 → page 60101", "testpage-part"),
            ["part-recordless-not-live"] = (
                () => TestPageShapeGap.Part("TestPage part 43 → page 60102", Detail),
                "TestPage part 43 → page 60102", "testpage-part"),
            ["part-not-live"] = (
                () => TestPageShapeGap.Part("TestPage part 44 → page 60103", Detail),
                "TestPage part 44 → page 60103", "testpage-part"),
            ["part-link-unresolved-part-field"] = (
                () => TestPageShapeGap.PartLink("TestPage part → page 60104 SubPageLink (FIELD)", Detail),
                "TestPage part → page 60104 SubPageLink (FIELD)", "testpage-part-link"),
            ["part-link-field-value-not-a-number"] = (
                () => TestPageShapeGap.PartLink("TestPage part → page 60105 SubPageLink", Detail),
                "TestPage part → page 60105 SubPageLink", "testpage-part-link"),
            ["control-binding"] = (
                () => TestPageShapeGap.ControlBinding("TestPage control 788108655", Detail),
                "TestPage control 788108655", "testpage-control-binding"),
            ["option-value-no-metadata"] = (
                () => TestPageShapeGap.OptionValue("TestPage control 12 on page 60106", Detail),
                "TestPage control 12 on page 60106", "testpage-option-value"),
            ["lookup-no-page-object"] = (
                () => TestPageShapeGap.Lookup("TestPage lookup on field 7", Detail),
                "TestPage lookup on field 7", "testpage-lookup"),
            ["drilldown-no-page-object"] = (
                () => TestPageShapeGap.DrillDown("TestPage drilldown on field 7", Detail),
                "TestPage drilldown on field 7", "testpage-drilldown"),

            // RunnerPageInstance.cs — four
            ["control-property-frozen-not-boolean"] = (
                () => TestPageShapeGap.ControlProperty("TestPage Visible on page 60107 element 3", Detail),
                "TestPage Visible on page 60107 element 3", "testpage-control-property"),
            ["control-property-live-not-boolean"] = (
                () => TestPageShapeGap.ControlProperty("TestPage Editable on page 60107 element 4", Detail),
                "TestPage Editable on page 60107 element 4", "testpage-control-property"),
            ["control-property-unevaluatable"] = (
                () => TestPageShapeGap.ControlProperty("TestPage Enabled on page 60107 element 5", Detail),
                "TestPage Enabled on page 60107 element 5", "testpage-control-property"),
            ["trigger-member-id-collision"] = (
                () => TestPageShapeGap.TriggerAmbiguity(
                    "TestPage OnLookup (member 123456)", "testpage-onlookup", Detail),
                "TestPage OnLookup (member 123456)", "testpage-onlookup"),
        };

    public static IEnumerable<object[]> SiteNames() => Sites.Keys.Select(k => new object[] { k });

    // ══ The two sites where the runner could not READ BC's own internals ═════════════════

    /// <summary>
    /// A SubPageLink whose <c>FilterType</c> is outside FIELD/CONST/FILTER. Measured on BC
    /// 28.1's <c>Microsoft.Dynamics.Nav.Types.dll</c>: the enum declares exactly CONST, FILTER
    /// and FIELD, and the runner's OWN emitter
    /// (<c>DependencyPageMetadataXml.EmitSubFormLinkXml</c>) writes only those three spellings.
    /// So a fourth value can only have come from BC's own compiled metadata — a read the runner
    /// performed and could not interpret, which is the BcShapeGapException case and not the
    /// runner failing to keep up.
    /// </summary>
    private static BcShapeGapException SubPageLinkKindGap() =>
        new("TestPage part → page 60108 SubPageLink",
            "Microsoft.Dynamics.Nav.Types.Metadata.FilterType",
            "holds 'Something', which is not one of FIELD/CONST/FILTER — the probe detail");

    /// <summary>
    /// Whether a source-table field declares an OnLookup could not be determined, because
    /// <c>RecordPatches.TryHasFieldLookupTrigger</c> could not reflect BC's private
    /// field-trigger backing fields. Its three-valued answer exists precisely so this outcome
    /// stays distinct from "the field declares no trigger" — and null is returned ONLY when the
    /// read failed, never when it succeeded and said no.
    /// </summary>
    private static BcShapeGapException FieldLookupTriggerGap() =>
        new("TestPage lookup on control 9 (page 60109)",
            "NCLMetaField.EventTriggerDataValue / EventTriggerData.LookupHandler",
            "backing field not found — the probe detail");

    public static IEnumerable<object[]> ShapeGaps() => new[]
    {
        new object[] { nameof(SubPageLinkKindGap) },
        new object[] { nameof(FieldLookupTriggerGap) },
    };

    private static BcShapeGapException ShapeGap(string name) =>
        name == nameof(SubPageLinkKindGap) ? SubPageLinkKindGap() : FieldLookupTriggerGap();

    // ══ The control arms — refusals in these same two files that must NOT move ═══════════

    /// <summary>
    /// A page with no <c>SourceTable</c> — MockTestPage.RequireRecord. #2999 lists it among the
    /// genuinely permanent ones: BC itself has no record-backed rowset for such a page, so
    /// an AL [TryFunction] reading `false` is the OBSERVABLE BC OUTCOME rather than a gap.
    /// </summary>
    private static RunnerOutOfScopeException PermanentNoSourceTable() =>
        new("TestPage page 60110 (Next())",
            "testpage-modal-no-source-table — this page has no SourceTable, so there is no "
            + "record-backed rowset for this operation. See docs/scope.md");

    /// <summary>
    /// A lookup that could only come from a TableRelation — RunnerPageInstance. Permanent for
    /// the same reason, and pinned from AL as well by
    /// tests/runner-extras/testpage-lookup-tablerelation-oos.
    /// </summary>
    private static RunnerOutOfScopeException PermanentTableRelationLookup() =>
        new("TestPage lookup on control 9 (page 60111)",
            "testpage-lookup — neither the control nor its source table field declares an "
            + "OnLookup trigger, so the lookup comes from a TableRelation and would open the "
            + "related table's list page, which the runner cannot stand up. See docs/scope.md");

    // ══ 1. The claim: in scope, not yet answerable for the shape found ═══════════════════

    [Theory]
    [MemberData(nameof(SiteNames))]
    public void Refusal_ClaimsNotYetImplemented_NotAPermanentScopeBoundary(string site)
    {
        var (build, api, surface) = Sites[site];
        var ex = build();

        Assert.Equal(api, ex.Api);
        // StartsWith, not Contains: IsPermanentOutOfScope reads the FIRST token, and
        // ExpectationManifest.ReasonAnchor cuts at the first em-dash separator.
        Assert.StartsWith("not-yet-implemented", ex.Reason, StringComparison.Ordinal);
        // The surface's own anchor survives as the second token, so the surfaces stay distinct
        // to a reader and to any future expectations entry.
        Assert.Contains(surface + ":", ex.Reason, StringComparison.Ordinal);
        // And the caller's detail is carried through rather than swallowed.
        Assert.Contains(Detail, ex.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFactoryOnTestPageShapeGap_ClaimsNotYetImplemented_AndNeverCitesScopeMd()
    {
        // Discovered, not listed: a factory added later is held to the same invariants without
        // anyone remembering to extend the table above.
        var factories = typeof(TestPageShapeGap)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.ReturnType == typeof(RunnerOutOfScopeException)
                        && m.GetParameters().All(p => p.ParameterType == typeof(string))
                        // Build is the private plumbing every factory delegates to; probing it
                        // proves nothing about a call site.
                        && m.Name != "Build")
            .ToList();

        Assert.True(factories.Count >= 8, $"expected at least the eight factories, found {factories.Count}");

        foreach (var m in factories)
        {
            var args = m.GetParameters().Select((_, i) => (object?)$"probe{i}").ToArray();
            var ex = (RunnerOutOfScopeException)m.Invoke(null, args)!;
            Assert.StartsWith("not-yet-implemented", ex.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("docs/scope.md", ex.Message, StringComparison.Ordinal);
        }
    }

    // ══ 2. The consequence: an AL [TryFunction] must NOT read a runner gap as `false` ════

    [Theory]
    [MemberData(nameof(SiteNames))]
    public void Refusal_TearsThroughATryFunction_InsteadOfReadingAsFalse(string site)
    {
        var (build, api, _) = Sites[site];

        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw build()));

        Assert.Equal(api, ex.Api);
    }

    [Fact]
    public void PermanentTestPageRefusal_IsStillTrappedByATryFunction_SoTheTestDiscriminatesOnTheClaim()
    {
        // CONTROL ARM ONE. Same exception TYPE, same two files, but surfaces #2999 measured as
        // genuinely permanent. Real BC in an environment that also lacks the surface answers
        // `false`, so trapping these is faithful — and a sweep that "fixed" them too would
        // fail here instead of shipping.
        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw PermanentNoSourceTable()));
        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw PermanentTableRelationLookup()));
    }

    // ══ 3. The sharper seam for TestPage work: asserterror ═══════════════════════════════

    [Theory]
    [MemberData(nameof(ShapeGaps))]
    public void ShapeGap_TearsThroughBothTryFunctionAndAssertError(string name)
    {
        // A BC-layout read that could not be performed is not a scope claim at all, so neither
        // of AL's error-trapping seams may absorb it. asserterror is the one that INVERTS a
        // result rather than hiding one: on real BC the call returns, so `asserterror Foo()`
        // FAILS; a runner that catches the gap makes it PASS.
        var tryFn = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => throw ShapeGap(name)));
        Assert.Equal(ShapeGap(name).Member, tryFn.Member);

        var asserted = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavMethodScope_AssertError(null!, () => throw ShapeGap(name)));
        Assert.Equal(ShapeGap(name).Surface, asserted.Surface);
    }

    [Fact]
    public void PermanentTestPageRefusal_IsStillCatchableByAssertError_SoTheTearThroughIsAboutTheClaim()
    {
        // CONTROL ARM TWO. Returning normally IS the pass signal — NavMethodScope_AssertError
        // throws NavNCLAssertErrorException when the body does NOT raise. Without this arm,
        // "anything that is not a plain exception tears through" would satisfy the assertion
        // above and prove nothing about the claim.
        BcRuntime.NavMethodScope_AssertError(null!, () => throw PermanentNoSourceTable());
        BcRuntime.NavMethodScope_AssertError(null!, () => throw PermanentTableRelationLookup());

        // And the fourteen corrected ones are STILL catchable by asserterror — #2871 is the
        // open question about that row and this change does not touch it. Only the two BC-shape
        // reads tear through here.
        BcRuntime.NavMethodScope_AssertError(
            null!, () => throw TestPageShapeGap.Lookup("TestPage lookup on field 7", Detail));
    }

    [Theory]
    [MemberData(nameof(ShapeGaps))]
    public void ShapeGap_IsNotAbsorbableAsAnOutOfScopeSignal(string name)
    {
        // The other half of why these two are a different type: anything carrying a
        // RunnerOutOfScopeException can be declared away by an `expect-oos` manifest entry, and
        // a BC-layout regression must never be declarable as an expected scope boundary — it is
        // a property of which BC build is on disk, so it can be true on one leg and false on
        // another in the same run.
        var gap = ShapeGap(name);

        Assert.DoesNotContain("out-of-scope: ", gap.Message, StringComparison.Ordinal);
        Assert.False(OutOfScopeMessage.TryParse(gap.Message, out _));
        Assert.Null(OutOfScopeMessage.FromException(gap));
        Assert.StartsWith("bc-shape-gap: ", gap.Message, StringComparison.Ordinal);
    }

    // ══ 4. The link: one of them, and it points at a section that exists ═════════════════

    [Theory]
    [MemberData(nameof(SiteNames))]
    public void Refusal_LinksToTheDocThatActuallyDocumentsTheLimit(string site)
    {
        var (build, _, _) = Sites[site];
        var msg = build().Message;

        Assert.EndsWith(" — see " + Doc, msg, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/scope.md", msg, StringComparison.Ordinal);

        // Counted on "see docs/", not on the " — see " separator: the defect at these sites was
        // a reason ending "See docs/scope.md" with BuildMessage appending its own link after
        // it, which leaves the separator count at 1 but renders the link twice (#2766/#2931).
        Assert.Equal(1, msg.Split("see docs/").Length - 1);
    }

    [Fact]
    public void TheDocAnchorsTheseRefusalsPointAt_Exist()
    {
        var limitations = File.ReadAllText(Path.Combine(RepoRoot, "docs", "limitations.md"));

        foreach (var anchor in new[] { "testpage-shape-gaps", "bc-shape-gaps" })
            Assert.Contains($"<a id=\"{anchor}\"></a>", limitations, StringComparison.Ordinal);
    }

    // ══ 5. The wire format the reporter and the expectations manifest read ═══════════════

    [Theory]
    [MemberData(nameof(SiteNames))]
    public void TypedAndUntypedRecovery_AgreeOnTheApiAndTheReason(string site)
    {
        var (build, api, _) = Sites[site];
        var ex = build();

        var typed = OutOfScopeMessage.FromException(ex);
        Assert.NotNull(typed);
        Assert.True(typed!.Value.Typed);
        Assert.Equal(api, typed.Value.Api);
        Assert.Equal(ex.Reason, typed.Value.Reason);

        // Untyped path: message text only, which is all a Cecil-injected throw site and the TRX
        // reader get. It cuts the api from the reason at the first " — ", so no api here may
        // contain that separator (#2945's Feature Key Modify defect). Note these apis DO carry
        // a "→", which is not the separator and is safe.
        Assert.True(OutOfScopeMessage.TryParse(ex.Message, out var parsed));
        Assert.Equal(api, parsed.Api);
        Assert.Equal(ex.Reason, parsed.Reason);
        Assert.DoesNotContain("docs/", parsed.Reason, StringComparison.Ordinal);
    }

    // ══ 6. The shape cannot drift back ══════════════════════════════════════════════════

    [Fact]
    public void AllSixteenCorrectedRefusalsStillExist_SoNoneWasDeletedRatherThanCorrected()
    {
        // A refusal DELETED rather than corrected means a precondition went back to being read
        // as a default, which is the failure this whole change is about. Asserted exactly, per
        // file, so a site that moves between the two files cannot hide in a total — and this is
        // also what covers the four sites the marker theories cannot address individually,
        // because two PAIRS of them render identical text (the two "could not be driven live"
        // branches, and the frozen/live "is not a Boolean" pair).
        var mock = CodeOf("MockTestPage.cs");
        var page = CodeOf("RunnerPageInstance.cs");

        Assert.Equal(10, Regex.Matches(mock, @"throw TestPageShapeGap\.").Count);
        Assert.Equal(1, Regex.Matches(mock, @"throw new AlRunner\.Infrastructure\.BcShapeGapException\(").Count);

        Assert.Equal(4, Regex.Matches(page, @"throw TestPageShapeGap\.").Count);
        Assert.Equal(1, Regex.Matches(page, @"throw new AlRunner\.Infrastructure\.BcShapeGapException\(").Count);
    }

    /// <summary>
    /// The source of the <c>throw</c> statement whose rendered text contains
    /// <paramref name="marker"/>. Bracketed from the nearest preceding <c>throw</c> to the
    /// statement's closing <c>);</c>, so the assertion is about ONE refusal rather than about
    /// the file as a whole.
    /// </summary>
    private static string ThrowStatementContaining(string file, string marker)
    {
        var code = FlattenedCodeOf(file);
        var idx = code.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, $"{file}: no refusal rendering '{marker}' — was it deleted rather than corrected?");
        var start = code.LastIndexOf("throw ", idx, StringComparison.Ordinal);
        var end = code.IndexOf(");", idx, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"{file}: could not bracket the throw around '{marker}'");
        return code[start..end];
    }

    /// <summary>The fourteen corrected sites, by the sentence each one renders.</summary>
    public static IEnumerable<object[]> CorrectedMarkers() => new[]
    {
        new object[] { "MockTestPage.cs", "could not resolve this control to a subpage part" },
        new object[] { "MockTestPage.cs", "the hosting page was built without an ITreeObject owner" },
        new object[] { "MockTestPage.cs", "the part's own page could not be driven live" },
        new object[] { "MockTestPage.cs", "the part's own field this link constrains could not be resolved" },
        new object[] { "MockTestPage.cs", "must be the parent's field number" },
        new object[] { "MockTestPage.cs", "nor to a page variable the runner could resolve" },
        new object[] { "MockTestPage.cs", "bound to an Option with no option metadata" },
        new object[] { "MockTestPage.cs", "so its OnLookup trigger cannot be reached" },
        new object[] { "MockTestPage.cs", "so its OnDrillDown trigger cannot be reached" },
        new object[] { "RunnerPageInstance.cs", "which is not a Boolean" },
        new object[] { "RunnerPageInstance.cs", "which cannot be evaluated:" },
        new object[] { "RunnerPageInstance.cs", "the runner cannot tell which trigger belongs to it" },
    };

    /// <summary>
    /// The eight permanent THROWS (nine citations — the option-value refusal spells the pointer
    /// on both branches of one ternary), by the sentence each one renders. The RunObject action
    /// is the ninth throw and is deliberately absent: see
    /// <see cref="RunnerPageInstance_KeepsItsTwoUncontestedPermanentCitations"/>.
    /// </summary>
    public static IEnumerable<object[]> KeptMarkers() => new[]
    {
        new object[] { "MockTestPage.cs", "this page has no SourceTable" },
        new object[] { "MockTestPage.cs", "OnQueryClosePage returned false" },
        new object[] { "MockTestPage.cs", "so it cannot be used to locate a row" },
        new object[] { "MockTestPage.cs", "is not an acceptable value" },
        new object[] { "MockTestPage.cs", "is not one of the option's values" },
        new object[] { "MockTestPage.cs", "is not the round-trip spelling TestPage" },
        new object[] { "RunnerPageInstance.cs", "would open the related table's list page" },
        new object[] { "RunnerPageInstance.cs", "so there is no table-field OnLookup to fall back to" },
    };

    [Theory]
    [MemberData(nameof(CorrectedMarkers))]
    public void CorrectedSite_KeepsItsSentenceAndDropsTheScopeMdClaim(string file, string marker)
    {
        // Per-site, not a count: the distinctive text of each corrected refusal must still be
        // in the file (it was corrected, not deleted) and its own throw must no longer claim
        // permanence. A count would pass even if a later change swapped WHICH sites carry it.
        Assert.DoesNotContain("docs/scope.md", ThrowStatementContaining(file, marker), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(KeptMarkers))]
    public void KeptRefusal_StillClaimsPermanence_SoTheClassificationWasNotQuietlyWidened(string file, string marker)
    {
        // CONTROL ARM THREE. #2999 lists the citations in these same two files that are
        // genuinely permanent and says explicitly not to sweep them. For these, an AL
        // [TryFunction] reading `false` IS the observable BC outcome, which is the whole test
        // for the bucket. A sweep that "fixed" them too fails here.
        Assert.Contains("docs/scope.md", ThrowStatementContaining(file, marker), StringComparison.Ordinal);
    }

    [Fact]
    public void MockTestPage_KeepsExactlyItsSixPermanentCitations()
    {
        // Exact, because nothing in flight reclassifies a MockTestPage refusal: six citations
        // across five throws (the option-value refusal spells the pointer on both branches of
        // one ternary). Under-sweep and over-sweep both fail.
        Assert.Equal(6, Regex.Matches(CodeOf("MockTestPage.cs"), "docs/scope\\.md").Count);
    }

    [Fact]
    public void RunnerPageInstance_KeepsItsTwoUncontestedPermanentCitations()
    {
        // NOT exact, and the reason is recorded rather than hidden: the third permanent
        // citation in this file is the RunObject action refusal, which #2931 reclassifies as
        // not-yet-implemented on the strength of a real-service-tier measurement (corpus PR
        // #172, all 8 BC legs). That site is #2931's to decide and is untouched here, so this
        // asserts the two lookup refusals #2999 named and allows either outcome for the third.
        var count = Regex.Matches(CodeOf("RunnerPageInstance.cs"), "docs/scope\\.md").Count;
        Assert.True(count is 2 or 3,
            $"expected the 2 TableRelation-lookup citations (+ optionally #2931's RunObject one), found {count}");
    }
}
