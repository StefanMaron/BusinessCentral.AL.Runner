// AllObjWithCaptionObjectSubtypeTests — issue #2326.
//
// WHAT THIS PINS, AND WHAT IT DELIBERATELY DOES NOT
//   The BC-behaviour claim — "AllObjWithCaption.\"Object Subtype\" carries a page's
//   PageType, a codeunit's Subtype, a table's TableType" — is asserted UPSTREAM, in
//   codeunit 60802 "Test AllObj Virtual Table" (corpus PR
//   StefanMaron/BusinessCentral.AL.Language.Tests#264), where a real service tier
//   adjudicates it on eight legs. Repeating it here would be the runner agreeing with
//   itself.
//
//   This file asserts something strictly runner-local: that RecordPatches'
//   ObjectSubtypeTextFor implements BC's per-kind rule, INCLUDING the one asymmetry that a
//   later tidy-up would most plausibly remove. BC blanks a Normal subtype for a CODEUNIT
//   only; a table or a query whose type is Normal reports the word. An edit that
//   "simplified" that into one uniform rule would be wrong in two directions at once, and
//   the corpus tests only notice once the pin has been bumped. The second asymmetry is
//   Install, which is empty for a reason one level upstream of this table — see
//   CodeunitWithInstallSubtype_IsEmpty_BecauseTheCompilerWritesNormal.
//
//   Source of the rule: Microsoft.Dynamics.Nav.Runtime.AllObjWithCaptionDataProvider
//   .GetCaptionAndSubtype, decompiled from Microsoft.Dynamics.Nav.Ncl.dll on BC 28.1. The
//   per-kind table is in docs/virtual-tables-allobj.md#object-subtype.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class AllObjWithCaptionObjectSubtypeTests
{
    // ----------------------------------------------------------------------------------
    // The asymmetry: Normal is blanked for a codeunit and ONLY for a codeunit.
    // ----------------------------------------------------------------------------------

    [Fact]
    public void CodeunitWithNormalSubtype_IsBlanked()
    {
        // BC: `subtype == CodeunitSubType.Normal ? string.Empty : EnumToString(subtype)`.
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor("Codeunit", "Normal"));
    }

    [Fact]
    public void TableWithNormalTableType_ReportsTheWordNormal()
    {
        // BC's Table branch calls EnumToString unconditionally — no Normal special case.
        // This is the half a uniform "blank Normal everywhere" rule would break.
        Assert.Equal("Normal", RecordPatches.ObjectSubtypeTextFor("Table", "Normal"));
    }

    [Fact]
    public void QueryWithNormalQueryType_ReportsTheWordNormal()
    {
        Assert.Equal("Normal", RecordPatches.ObjectSubtypeTextFor("Query", "Normal"));
    }

    // ----------------------------------------------------------------------------------
    // Install: empty, and NOT because Install is blanked.
    //
    // The AL compiler does not carry Install into object metadata — NCLMetaCodeunit.Subtype
    // reads the codeunit's NavCodeunitOptionsAttribute, which is what the compiler WROTE,
    // and for an Install codeunit that is Normal. BC's provider therefore sees Normal here
    // and blanks it, so the value lands on the empty string by two steps.
    //
    // Five BC legs adjudicated this directly. The first version of the upstream test
    // asserted 'Install'; 27.0, 27.3, 27.5, 28.2 and 28.3 each answered the empty string,
    // while the other seven tests in the same prefix passed on every one of them. Same
    // constant and same reason as ResolveCodeunitSubtypeOrdinal, whose own measurement
    // (1,690 Base Application codeunits, not one carrying a 4) is on
    // AlSubtypeTheCompilerDoesNotEmit.
    // ----------------------------------------------------------------------------------

    [Fact]
    public void CodeunitWithInstallSubtype_IsEmpty_BecauseTheCompilerWritesNormal()
    {
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor("Codeunit", "Install"));
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor("Codeunit", "install"));
    }

    [Fact]
    public void InstallCollapse_IsCodeunitOnly()
    {
        // The collapse belongs to the codeunit branch. "Install" is not a member of
        // TableType, PageType or QueryType, so no real object reaches these — but a
        // translation written above the kind test rather than inside it would blank them,
        // and nothing else in this file would notice.
        Assert.Equal("Install", RecordPatches.ObjectSubtypeTextFor("Page", "Install"));
        Assert.Equal("Install", RecordPatches.ObjectSubtypeTextFor("Table", "Install"));
    }

    // ----------------------------------------------------------------------------------
    // The ordinary direction: a declared subtype is carried through verbatim, for every
    // kind that has one. Distinct values per kind, so a constant-returning implementation
    // fails.
    // ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Page", "RoleCenter")]
    [InlineData("Page", "Card")]
    [InlineData("Page", "List")]
    [InlineData("Codeunit", "Test")]
    [InlineData("Codeunit", "Upgrade")]
    [InlineData("Codeunit", "TestRunner")]
    [InlineData("Table", "CRM")]
    [InlineData("Table", "Temporary")]
    [InlineData("Query", "API")]
    public void DeclaredSubtype_IsCarriedThroughVerbatim(string kind, string subtype)
    {
        Assert.Equal(subtype, RecordPatches.ObjectSubtypeTextFor(kind, subtype));
    }

    // ----------------------------------------------------------------------------------
    // "Declares none" and "no subtype concept" both land on the empty string, which is what
    // BC's emptySubtype is. Null is how the inventory spells both.
    // ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Report")]
    [InlineData("XMLport")]
    [InlineData("Enum")]
    [InlineData("PageExtension")]     // known gap: BC answers the target object's id here
    [InlineData("TableExtension")]
    [InlineData("Codeunit")]
    [InlineData("Page")]
    public void NullSubtype_IsTheEmptyString(string kind)
    {
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor(kind, null));
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor(kind, string.Empty));
    }

    // ----------------------------------------------------------------------------------
    // The kind is matched the way every other AllObj/AllObjWithCaption lookup matches it —
    // through NormalizeObjectTypeName — so a spelling difference cannot silently turn the
    // codeunit rule off. A "CodeUnit"/"codeunit" mismatch would leave the word 'Normal' in
    // the column and no test would see it without this.
    // ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("codeunit")]
    [InlineData("CODEUNIT")]
    [InlineData("CodeUnit")]
    public void CodeunitKindMatch_IsSpellingInsensitive(string kind)
    {
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor(kind, "Normal"));
        // ...and a subtype the compiler DOES carry still comes through under the same
        // spellings, so the match is not simply blanking everything it recognises.
        Assert.Equal("Test", RecordPatches.ObjectSubtypeTextFor(kind, "Test"));
    }

    [Fact]
    public void NormalMatch_IsCaseInsensitiveToo()
    {
        // BC compares enum VALUES, not strings; the runner reads a property as written, so
        // the equivalent comparison here has to ignore case or an AL author's `Subtype =
        // normal;` would report the word.
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor("Codeunit", "normal"));
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor("Codeunit", "NORMAL"));
    }

    // ----------------------------------------------------------------------------------
    // Negative control for the whole file: a value that merely CONTAINS "Normal" is not
    // Normal, and must not be blanked for a codeunit.
    // ----------------------------------------------------------------------------------

    [Fact]
    public void SubtypeMerelyContainingNormal_IsNotBlanked()
    {
        Assert.Equal("NormalX", RecordPatches.ObjectSubtypeTextFor("Codeunit", "NormalX"));
        Assert.Equal("Abnormal", RecordPatches.ObjectSubtypeTextFor("Codeunit", "Abnormal"));
    }

    // ----------------------------------------------------------------------------------
    // Issue #3392 — the five *extension kinds, whose rule differs in KIND and not in value:
    // BC answers the TARGET OBJECT'S ID as a decimal string, never a type name.
    //
    // The BC-behaviour claim is asserted upstream (corpus PR
    // StefanMaron/BusinessCentral.AL.Language.Tests#291). What is pinned here is the
    // runner-local mapping deciding WHICH id namespace each kind's target is resolved in.
    // Getting that wrong does not fail loudly: AL gives every object kind its own id
    // namespace, so resolving a pageextension's target among tables answers a plausible
    // WRONG NUMBER rather than nothing — the hazard TryGetObjectNameOfKind exists for in
    // the other direction (#2943), here in the direction nothing was guarding.
    // ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("PageExtension", "Page")]
    [InlineData("TableExtension", "Table")]
    [InlineData("EnumExtension", "Enum")]
    [InlineData("PermissionSetExtension", "PermissionSet")]
    [InlineData("ReportExtension", "Report")]
    public void ExtensionKind_ResolvesItsTargetInThatKindsOwnNamespace(string kind, string targetKind)
    {
        Assert.Equal(targetKind, RecordPatches.ExtensionTargetObjectKind(kind));
    }

    [Theory]
    [InlineData("pageextension")]
    [InlineData("PAGEEXTENSION")]
    [InlineData("PageExtension")]
    public void ExtensionKindMatch_IsSpellingInsensitive(string kind)
    {
        // Same NormalizeObjectTypeName route as the codeunit rule above. A spelling
        // mismatch here would leave the column empty, which is indistinguishable from the
        // pre-fix behaviour and so would pass every "is it non-empty" style check.
        Assert.Equal("Page", RecordPatches.ExtensionTargetObjectKind(kind));
    }

    [Theory]
    // NOT every kind whose name ends in "Extension". These two are the reason the mapping
    // is an explicit list rather than a suffix test:
    //   * QueryExtension is absent from BC's shared switch arm AND from
    //     AllObjWithCaption's own "Object Type" option set.
    //   * ProfileExtension IS in that option set but is NOT in the switch arm, so BC
    //     leaves it on the initial `string.Empty`.
    [InlineData("QueryExtension")]
    [InlineData("ProfileExtension")]
    // ...and the kinds that carry a real subtype must not be diverted into id resolution.
    [InlineData("Table")]
    [InlineData("Page")]
    [InlineData("Query")]
    [InlineData("Codeunit")]
    [InlineData("Report")]
    [InlineData("XMLport")]
    [InlineData("Enum")]
    public void KindsBcDoesNotAnswerWithATargetId_HaveNoTargetNamespace(string kind)
    {
        Assert.Null(RecordPatches.ExtensionTargetObjectKind(kind));
    }

    [Fact]
    public void ExtensionWithAnUnresolvableTarget_IsEmptyRatherThanEchoingTheName()
    {
        // BC's arm ends in `?? string.Empty` — a target the app group cannot summarise
        // yields the empty string. The runner must do the same rather than fall through to
        // the non-extension path, which would put the target's NAME in a column whose value
        // is documented to be an id. No object of any kind carries this name, so nothing
        // resolves and the fallback is what is being read.
        Assert.Equal(
            string.Empty,
            RecordPatches.ObjectSubtypeTextFor("TableExtension", "AXS No Such Object Anywhere"));
    }

    [Fact]
    public void ExtensionDeclaringNoTarget_IsEmpty()
    {
        // Null and empty reach here from an extension whose target the parse could not read.
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor("PageExtension", null));
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor("PageExtension", string.Empty));
    }

    [Fact]
    public void ExtensionKindDoesNotTakeTheCodeunitNormalRule()
    {
        // A tableextension over a table literally named "Normal" resolves as a name like any
        // other. Written because the codeunit blanking test sits directly above this one and
        // is the edit most likely to be widened by mistake; the empty answer here is the
        // unresolvable-target fallback, NOT the Normal rule, which is why the assertion
        // below distinguishes them.
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor("TableExtension", "Normal"));
        // The codeunit rule, by contrast, blanks the WORD without consulting any inventory —
        // so it still blanks it when an object of that name would have resolved.
        Assert.Equal(string.Empty, RecordPatches.ObjectSubtypeTextFor("Codeunit", "Normal"));
    }
}
