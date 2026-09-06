// MetadataOptionColumnOrdinalTests — where Page Metadata (2000000138) and CodeUnit Metadata
// (2000000137) get the ordinal of their one option column from, and what they do with a member
// the column's own option string does not list (#3080).
//
// THE TWO COLUMNS LOOK IDENTICAL AND ANSWER DIFFERENTLY, AND A TIER DECIDED BOTH
// -----------------------------------------------------------------------------
// Both columns are filled the same way in Ncl — `GetOptionValue(5, (int)<some runtime enum>)`
// — and in both cases that enum reaches past the last member the column names. For PageType
// the extra ordinal comes through: a `PageType = PromptDialog` page reports 20, past
// HeadlinePart at 12. For SubType it does not: a `Subtype = Install` codeunit reports 0,
// `Normal`, even though CodeunitSubType numbers Install at 4.
//
// Both were measured on real BC, not reasoned about, and the second one contradicted the
// reasoning: StefanMaron/BusinessCentral.AL.Language.Tests#196 (PageType, 20) and #201
// (SubType, 0 for Install and 1 for Test in the same run) each ran on eight Cloud legs, 27.0
// through 28.4. The reason for the difference is one frame upstream of the provider — the
// value handed to the SubType column is what the AL COMPILER wrote into
// NavCodeunitOptionsAttribute, and the compiler emits Test, TestRunner and Upgrade but never
// Install. Measured over Base Application 28.1's own assemblies: of 1,690 codeunits carrying
// that attribute, 28 of 28 declared-Upgrade carry 3, 2 of 2 declared-TestRunner carry 2, and
// all three declared-Install (3999, 5000, 7582) carry 0. Not one carries 4.
//
// WHY THIS IS A RUNNER-SIDE MECHANISM TEST AND NOT (ONLY) AN AL BUNDLE
// -------------------------------------------------------------------
// The BC-behaviour claims are upstream, in the two corpus PRs above. Nothing here restates
// them.
//
// What this file pins is the ROUTE, which no AL assertion can see. An AL test only ever
// observes the artifact it runs on, and on that artifact each column's option string and the
// runtime enum agree wherever they overlap — so a test written against one cannot tell which
// of the two the runner read, nor whether the runner reached the right answer for the right
// reason. The cases below hand the resolvers an option string and an enum that DISAGREE, and
// hand the SubType resolver a map that offers Install at 4, which is exactly what no single
// artifact can construct.
//
// The enums below are copied verbatim out of BC 28.1.49838.53910's own
// Microsoft.Dynamics.Nav.Types.dll, and BcEnumsStillReachBeyondTheOptionString checks that
// copy against the artifact the leg actually built with, so it cannot rot into a fiction.

using System;
using System.Collections.Generic;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class MetadataOptionColumnOrdinalTests
{
    // BC 28.1.49838.53910's own OptionMembers for the two columns, read out of System.app's
    // PageMetadata.Table.al and CodeUnitMetadata.Table.al. Both are a strict PREFIX of the
    // runtime enum the provider casts — which is the whole subject of this file.
    private const string RealPageTypeOptions =
        "Card,List,RoleCenter,CardPart,ListPart,Document,Worksheet,ListPlus,ConfirmationDialog,"
        + "NavigatePage,StandardDialog,API,HeadlinePart";
    private const string RealSubTypeOptions = "Normal,Test,TestRunner,Upgrade";

    /// <summary>Microsoft.Dynamics.Nav.Types.Metadata.PageType, verbatim.</summary>
    private enum BcPageType
    {
        Card, List, RoleCenter, CardPart, ListPart, Document, Worksheet, ListPlus,
        ConfirmationDialog, NavigatePage, StandardDialog, API, HeadlinePart, ReportPreview,
        ReportProcessingOnly, XmlPort, ReportViewer, FilterPage, ListQuery, BannerPart,
        PromptDialog, ConfigurationDialog, UserControlHost
    }

    /// <summary>Microsoft.Dynamics.Nav.Types.CodeunitSubType, verbatim.</summary>
    private enum BcCodeunitSubType { Normal, Test, TestRunner, Upgrade, Install }

    private static Dictionary<string, int> PageTypeMap(string optionString = RealPageTypeOptions)
        => RecordPatches.BuildMetadataOptionOrdinals(optionString, typeof(BcPageType));

    // No enum, matching production: EnsureCodeunitSubtypeOrdinals passes bcRuntimeEnum: null
    // for this column. SubTypeMapOverlaid below is the adversarial one.
    private static Dictionary<string, int> SubTypeMap(string optionString = RealSubTypeOptions)
        => RecordPatches.BuildMetadataOptionOrdinals(optionString, bcRuntimeEnum: null);

    private static int Page(string? declared, string optionString = RealPageTypeOptions)
        => RecordPatches.ResolvePageTypeOrdinal(PageTypeMap(optionString), optionString, declared, pageId: 60017);

    private static int Codeunit(string? declared, string optionString = RealSubTypeOptions)
        => RecordPatches.ResolveCodeunitSubtypeOrdinal(SubTypeMap(optionString), optionString, declared, codeunitId: 60963);

    // ── The defect: a member past the end of the option string ───────────────────────────

    [Fact]
    public void PageTypeBeyondTheOptionString_ResolvesTheRuntimeEnumOrdinal_NotCard()
    {
        // BC 28.1's PageType option string stops at HeadlinePart (12). PromptDialog and
        // UserControlHost are real, compiler-accepted AL — Base Application 28.1 ships one page
        // of each (5836 "Copilot Marketing Text", 6324 "Power BI Element Addin Host") — and
        // PageDataProvider writes GetOptionValue(5, (int)properties.PageType), so a service
        // tier reports the ENUM ordinal even though the column names no member there.
        //
        // Before #3080 both fell through to NavValue.GetDefaultNavValue, i.e. 0, so the runner
        // said "Card" about a page that declared neither. 0 is the answer this must not give.
        Assert.Equal(20, Page("PromptDialog"));
        Assert.Equal(22, Page("UserControlHost"));

        // Distinguishing 20 from 22 is the point: a fix that answered "the count of members"
        // or any other single constant would satisfy neither pair.
        Assert.NotEqual(Page("PromptDialog"), Page("UserControlHost"));
    }

    [Fact]
    public void DeclaredInstallSubtype_ResolvesNormal_NotTheRuntimeEnumOrdinal()
    {
        // CodeunitSubType is Normal,Test,TestRunner,Upgrade,Install — five members — while the
        // column lists the first four, so the shape looks exactly like the PageType case above
        // and the analogous answer would be 4. A service tier says otherwise: corpus #201 reads
        // an Install fixture through this column on all eight legs and gets 0.
        Assert.Equal(0, Codeunit("Install"));
        Assert.Equal(Codeunit("Normal"), Codeunit("Install"));
        Assert.NotEqual(4, Codeunit("Install"));

        // And the column is not simply always 0 — the three subtypes the compiler DOES emit
        // keep their own ordinals, so the Install translation flattened nothing else. This is
        // the same contrast corpus #201 draws by reading a Subtype = Test codeunit in the same
        // run as the Install one.
        Assert.Equal(1, Codeunit("Test"));
        Assert.Equal(2, Codeunit("TestRunner"));
        Assert.Equal(3, Codeunit("Upgrade"));
    }

    [Fact]
    public void DeclaredInstallSubtype_ResolvesTheNormalMemberByName_NotAHardcodedZero()
    {
        // On the real column Normal is at 0, so "translated to Normal" and "returned 0" are
        // indistinguishable there. Reordered, they are not: a resolver that hardcoded 0 would
        // report Test about an Install codeunit.
        const string Reordered = "Test,Upgrade,Normal";
        Assert.Equal(2, Codeunit("Install", Reordered));
        Assert.Equal(0, Codeunit("Test", Reordered));
    }

    [Fact]
    public void DeclaredInstallSubtype_StaysNormal_EvenWhenAnInstallOrdinalIsOnOffer()
    {
        // The translation is unconditional and sits in FRONT of the lookup, so a map that does
        // carry install -> 4 cannot produce 4. Two ways such a map can arise, and the answer is
        // the tier's either way:
        //
        //   (a) somebody overlays the runtime enum onto this column — which is what the first
        //       shape of #3080's fix did, and what the tier rejected;
        //   (b) a future artifact's column names Install as a fifth member. Even then an
        //       Install codeunit reports 0, because the value reaching the column is the
        //       compiler's, not the AL author's.
        //
        // If a future AL compiler ever starts emitting Install, corpus #201 goes red on that
        // version and this translation is what has to change. That is the intended alarm.
        var overlaid = RecordPatches.BuildMetadataOptionOrdinals(RealSubTypeOptions, typeof(BcCodeunitSubType));
        Assert.Equal(4, overlaid[NormalizedFor("Install")]);   // the map really is offering 4
        Assert.Equal(0, RecordPatches.ResolveCodeunitSubtypeOrdinal(
            overlaid, RealSubTypeOptions, "Install", codeunitId: 60963));

        const string ColumnNamingInstall = "Normal,Test,TestRunner,Upgrade,Install";
        Assert.Equal(0, Codeunit("Install", ColumnNamingInstall));
    }

    // ── The half that already worked, which the fix must not move ────────────────────────

    [Fact]
    public void MembersTheOptionStringDoesList_KeepTheirOrdinals()
    {
        Assert.Equal(0, Page("Card"));
        Assert.Equal(1, Page("List"));
        Assert.Equal(12, Page("HeadlinePart"));
        Assert.Equal(0, Codeunit("Normal"));
        Assert.Equal(1, Codeunit("Test"));
        Assert.Equal(2, Codeunit("TestRunner"));
    }

    [Fact]
    public void TheRuntimeEnumWins_WhenTheOptionStringDisagreesAboutASharedName()
    {
        // The two sources cannot disagree on any artifact measured so far — the option string
        // is a prefix of the enum — but BC's providers consult only the enum, so if they ever
        // did, the enum is the answer. Constructed here because no artifact can construct it:
        // an option string listing List first would put List at 0 by position, and the enum
        // says 1.
        const string Reordered = "List,Card,RoleCenter";
        Assert.Equal(1, Page("List", Reordered));
        Assert.Equal(0, Page("Card", Reordered));
    }

    [Fact]
    public void WithoutTheRuntimeEnum_TheOptionStringStillAnswersEveryMemberItLists()
    {
        // The degraded path: a BC build where the enum type cannot be resolved. Every member
        // the column names still resolves, so the runner keeps working; only the members past
        // the end of the option string become unanswerable.
        var optionOnly = RecordPatches.BuildMetadataOptionOrdinals(RealPageTypeOptions, bcRuntimeEnum: null);

        Assert.Equal(0, optionOnly[NormalizedFor("Card")]);
        Assert.Equal(12, optionOnly[NormalizedFor("HeadlinePart")]);
        Assert.False(optionOnly.ContainsKey(NormalizedFor("PromptDialog")));

        // And this is exactly where the wrong answer came from before #3080, because the map
        // was ALWAYS this one: the miss fell through to NavValue.GetDefaultNavValue — ordinal
        // 0, "Card" — about a page that declared PromptDialog. Unanswerable is the truthful
        // outcome; 0 is the one that must never come back.
        var refusal = Assert.Throws<RunnerOutOfScopeException>(
            () => RecordPatches.ResolvePageTypeOrdinal(optionOnly, RealPageTypeOptions, "PromptDialog", 5836));
        Assert.Contains("PromptDialog", refusal.Message);
    }

    // NormalizeObjectTypeName is private to RecordPatches; these two names contain nothing it
    // strips, so the normalized key is the lower-cased name.
    private static string NormalizedFor(string member) => member.ToLowerInvariant();

    // ── The default, and the two refusals ────────────────────────────────────────────────

    [Fact]
    public void UndeclaredPageType_ResolvesCardByName_NotOrdinalZero()
    {
        // Both parsers already substitute "Card" before a row is built, so this branch is the
        // backstop for a null that slips through (a stale symbol payload, a cache version that
        // predates the property). It has to resolve the MEMBER, not a position: on the real
        // artifact Card is 0 and the two are indistinguishable, so the second case is what
        // makes the assertion mean anything.
        Assert.Equal(0, Page(null));
        Assert.Equal(0, Page("   "));
        Assert.Equal(0, Page(null, "List,RoleCenter,Card"));   // enum wins: Card is 0 there too

        // With no enum to consult, the position in the option string is the answer, and it
        // moves with the member rather than staying at 0.
        var reorderedOptionOnly = RecordPatches.BuildMetadataOptionOrdinals("List,RoleCenter,Card", null);
        Assert.Equal(2, RecordPatches.ResolvePageTypeOrdinal(
            reorderedOptionOnly, "List,RoleCenter,Card", null, pageId: 60017));
    }

    [Fact]
    public void UndeclaredSubtype_ResolvesNormalByName_NotOrdinalZero()
    {
        Assert.Equal(0, Codeunit(null));

        var reorderedOptionOnly = RecordPatches.BuildMetadataOptionOrdinals("Test,Upgrade,Normal", null);
        Assert.Equal(2, RecordPatches.ResolveCodeunitSubtypeOrdinal(
            reorderedOptionOnly, "Test,Upgrade,Normal", null, codeunitId: 60963));
    }

    [Fact]
    public void PageTypeInNeitherSource_IsRefusedNotDefaulted()
    {
        // Unreachable from valid AL — the compiler validates PageType against the same enum —
        // and that is exactly why answering 0 would be undetectable. Refusing here is only safe
        // BECAUSE the enum overlay covers PromptDialog and UserControlHost; refusing on the
        // option string alone would have thrown on every run that loads the Base Application.
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => Page("NotAPageType"));

        Assert.Contains("60017", ex.Message);
        Assert.Contains("declares PageType = 'NotAPageType'", ex.Message);
        Assert.Contains(RealPageTypeOptions, ex.Message);
        // Category (2) of RecordPatches.VirtualTableShapeGap.cs: in scope, not answerable yet.
        // The anchor is load-bearing — IsPermanentOutOfScope reads it to decide whether an AL
        // [TryFunction] traps this into `false` or it tears through.
        Assert.StartsWith("not-yet-implemented", ex.Reason);
        Assert.Equal("Page Metadata (virtual table 2000000138)", ex.Api);
    }

    [Fact]
    public void SubtypeTheColumnDoesNotName_IsRefusedNotDefaulted()
    {
        // Unreachable from valid AL, and that is exactly why answering 0 would be undetectable.
        // Refusing here is safe BECAUSE the Install translation runs first: the one subtype AL
        // accepts that this column does not name never reaches this branch, so it can only fire
        // on something that is not an AL codeunit subtype at all.
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => Codeunit("NotASubtype"));

        Assert.Contains("60963", ex.Message);
        Assert.Contains("declares Subtype = 'NotASubtype'", ex.Message);
        Assert.Contains(RealSubTypeOptions, ex.Message);
        // The message has to say why "not in the option string" is the right test for this
        // column when it is the wrong test for PageType, or the next reader re-derives it.
        Assert.Contains("Install", ex.Message);
        Assert.StartsWith("not-yet-implemented", ex.Reason);
        Assert.Equal("CodeUnit Metadata (virtual table 2000000137)", ex.Api);

        // Install itself is NOT refused — it is translated. The refusal and the translation are
        // the two halves of the same decision and this pins that they did not get swapped.
        Assert.Equal(0, Codeunit("Install"));
    }

    [Fact]
    public void UndeclaredValue_DefaultMemberAbsentFromBothSources_IsRefusedNotDefaulted()
    {
        // The mirror of the Table Metadata refusal #3019 added: an artifact whose column does
        // not carry the AL default at all. Answering 0 would report whichever member happens to
        // be first about an object that said nothing.
        const string WithoutCard = "List,RoleCenter,Document";
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.ResolvePageTypeOrdinal(
            RecordPatches.BuildMetadataOptionOrdinals(WithoutCard, null), WithoutCard, null, pageId: 60017));

        Assert.Contains("declares no PageType", ex.Message);
        Assert.Contains("'Card'", ex.Message);
        Assert.Contains(WithoutCard, ex.Message);

        const string WithoutNormal = "Test,TestRunner,Upgrade";
        var ex2 = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.ResolveCodeunitSubtypeOrdinal(
            RecordPatches.BuildMetadataOptionOrdinals(WithoutNormal, null), WithoutNormal, null, codeunitId: 60963));

        Assert.Contains("declares no Subtype", ex2.Message);
        Assert.Contains("'Normal'", ex2.Message);

        // A declared Install on that same column reaches the same refusal, by the same route —
        // the translation makes it a request for "Normal", which this column does not name.
        // Refused, not quietly answered as one of the three members it does name.
        var ex3 = Assert.Throws<RunnerOutOfScopeException>(() => RecordPatches.ResolveCodeunitSubtypeOrdinal(
            RecordPatches.BuildMetadataOptionOrdinals(WithoutNormal, null), WithoutNormal, "Install", codeunitId: 60963));
        Assert.Contains("'Install'", ex3.Message);
    }

    // ── The copies above, checked against the artifact this leg actually built with ──────

    /// <summary>BC's runtime enum behind CodeUnit Metadata's SubType column. Not a constant in
    /// AlRunner any more — the production resolver deliberately does not consult it (#3080) —
    /// so it lives here, where the only thing it is used for is keeping the verbatim copy of
    /// it above honest.</summary>
    private const string BcCodeunitSubTypeEnumName = "Microsoft.Dynamics.Nav.Types.CodeunitSubType";

    [Fact]
    public void BcEnumsStillReachBeyondTheOptionString_OnTheArtifactUnderTest()
    {
        // AlRunner.Tests references Microsoft.Dynamics.Nav.Types out of the resolved artifact,
        // so this binds to whichever BC version the leg is running — 27.0 through 28.4 across
        // the matrix. It is the guard that keeps the verbatim copies above honest, and the
        // guard that the runner is naming a type that exists.
        var pageType = RecordPatches.ResolveBcOptionEnum(RecordPatches.BcPageTypeEnumName);
        var subType = RecordPatches.ResolveBcOptionEnum(BcCodeunitSubTypeEnumName);

        Assert.True(pageType != null, $"{RecordPatches.BcPageTypeEnumName} did not resolve on this BC build.");
        Assert.True(subType != null, $"{BcCodeunitSubTypeEnumName} did not resolve on this BC build.");

        // BOTH enums carry members their column cannot name. For PageType that is the premise
        // of the overlay; for CodeunitSubType it is the premise that turned out to be
        // necessary but NOT sufficient, which is why it is asserted here and acted on only
        // there. If Install ever stopped being past the end of that column, the comment above
        // explaining why this file treats the two columns differently would be stale.
        Assert.Contains("PromptDialog", Enum.GetNames(pageType!));
        Assert.Contains("UserControlHost", Enum.GetNames(pageType!));
        Assert.Contains("Install", Enum.GetNames(subType!));
        Assert.True(
            Convert.ToInt32(Enum.Parse(subType!, "Install")) >= RealSubTypeOptions.Split(',').Length,
            "CodeunitSubType.Install is no longer past the last member the SubType column names.");

        // PageType: the runner's answer for a member past the end is the enum's own value,
        // whatever this version numbers it — asserted against the enum rather than against 20,
        // so a BC release that inserts a member does not make this test wrong about BC.
        var live = RecordPatches.BuildMetadataOptionOrdinals(RealPageTypeOptions, pageType);
        Assert.Equal(
            Convert.ToInt32(Enum.Parse(pageType!, "PromptDialog")),
            RecordPatches.ResolvePageTypeOrdinal(live, RealPageTypeOptions, "PromptDialog", 5836));

        // SubType: the runner's answer is NOT the enum's value, and this is the assertion that
        // says so against the live artifact rather than against a copy. What a service tier
        // reports for an Install codeunit is Normal's ordinal, on every leg (corpus #201).
        Assert.NotEqual(
            Convert.ToInt32(Enum.Parse(subType!, "Install")),
            RecordPatches.ResolveCodeunitSubtypeOrdinal(
                SubTypeMap(), RealSubTypeOptions, "Install", codeunitId: 60963));
        Assert.Equal(0, RecordPatches.ResolveCodeunitSubtypeOrdinal(
            SubTypeMap(), RealSubTypeOptions, "Install", codeunitId: 60963));
    }
}
