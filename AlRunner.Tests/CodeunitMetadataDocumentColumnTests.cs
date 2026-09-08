// CodeunitMetadataDocumentColumnTests — how four CodeUnit Metadata (2000000137) columns are
// read out of BC's own emitted metadata document, and what happens to a document that states
// something the runner cannot spell (#3606).
//
// WHY THIS IS A RUNNER-SIDE MECHANISM TEST AND NOT (ONLY) AN AL BUNDLE
// -------------------------------------------------------------------
// The BC-behaviour claims are upstream, in corpus PR 296 — a TestRunner reporting each
// declared TestIsolation, a Subtype = Test codeunit reporting None where an ordinary one
// reports Disabled, an X-declaring codeunit reporting 'X' while its sibling column stays
// empty, and a namespaced codeunit reporting its full dotted namespace. Nothing here restates
// them; the same four assertions run there against eight real service tiers.
//
// What this file pins is what no AL assertion can reach. An AL test observes only the
// documents the AL compiler chose to emit for the bundle it ran on, so it cannot present:
//
//   * a TestIsolation value the column's own option string does not name — AL0223 stops the
//     property on anything but a TestRunner, and the compiler writes only members the column
//     has, so the refusal path has no AL spelling at all;
//   * a permission mask other than X — AL0195 rejects every other kind on a codeunit, so the
//     R/I/M/D letters and the lowercase indirect half are unreachable from AL even though the
//     same two columns exist on Table and Page Metadata where the mask is not restricted;
//   * a malformed document, which is a BC emitter shape change rather than anything AL can
//     declare;
//   * an ABSENT ALNamespace attribute as distinct from one stated as the empty string. BC
//     emits ALNamespace="" for an un-namespaced object, so the two are the same answer on any
//     real bundle and a test written against one cannot tell which the runner read.
//
// The permission spelling asserted below is BC's own, read out of Ncl.dll:
// MetadataDataProvider.permissions decodes to R,I,M,D,X and CreatePermissionMaskString
// lowercases the letter for the indirect bit at n+5. See
// AlRunner/Patches/RecordPatches.CodeunitMetadataFromBcDocument.cs.

using System.Collections.Generic;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class CodeunitMetadataDocumentColumnTests
{
    // BC 28.1.49838.53910's own OptionMembers for the RequiredTestIsolation column, read out
    // of System.app's CodeUnitMetadata.Table.al. Matches Types.TestCodeunitRequiredTestIsolation
    // member for member, unlike the SubType column beside it — see
    // MetadataOptionColumnOrdinalTests for that contrast.
    private const string RealIsolationOptions = "None,Disabled,Codeunit,Function";

    private static Dictionary<string, int> IsolationMap(string optionString = RealIsolationOptions)
        => RecordPatches.BuildMetadataOptionOrdinals(optionString, bcRuntimeEnum: null);

    /// <summary>A document with exactly the attributes named, in BC's own emitted shape.</summary>
    private static string Document(string attributes) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + $"<CodeUnit MetadataVersion=\"130000\" ID=\"60963\" Name=\"ALT Codeunit Meta Probe\" {attributes} "
        + "EventSubscriberInstance=\"StaticAutomatic\" SingleInstance=\"0\" "
        + "xmlns=\"urn:schemas-microsoft-com:dynamics:NAV:MetaObjects\" />";

    private static RecordPatches.BcCodeunitDocumentValues Parse(
        string attributes, string optionString = RealIsolationOptions)
        => RecordPatches.ParseCodeunitMetadataDocument(
            Document(attributes), codeunitId: 60963, IsolationMap(optionString));

    // ── RequiredTestIsolation: the ordinal comes from the COLUMN, not from a constant ──────

    [Fact]
    public void EachDeclaredTestIsolation_ResolvesItsOwnMemberOrdinal()
    {
        // The compiler writes the member name; the ordinal has to come from the column's own
        // option string. Asserting all three, and that they differ, is what a resolver
        // answering one constant cannot satisfy.
        Assert.Equal(1, Parse("TestIsolation=\"Disabled\"").RequiredTestIsolationOrdinal);
        Assert.Equal(2, Parse("TestIsolation=\"Codeunit\"").RequiredTestIsolationOrdinal);
        Assert.Equal(3, Parse("TestIsolation=\"Function\"").RequiredTestIsolationOrdinal);
    }

    [Fact]
    public void DeclaredTestIsolation_IsLookedUpByName_NotByPosition()
    {
        // On the real column Disabled sits at 1, so "resolved the Disabled member" and
        // "returned 1" are indistinguishable there. Reordered they are not — and no artifact
        // can present a reordered column, which is why this case only exists here.
        const string Reordered = "Function,None,Codeunit,Disabled";
        Assert.Equal(3, Parse("TestIsolation=\"Disabled\"", Reordered).RequiredTestIsolationOrdinal);
        Assert.Equal(0, Parse("TestIsolation=\"Function\"", Reordered).RequiredTestIsolationOrdinal);
    }

    [Fact]
    public void AbsentTestIsolation_IsLeftToBcsDefault_NotMappedOntoAMember()
    {
        // -1 is this parse's "the document states nothing this column can carry", and the row
        // builder turns it into NavValue.GetDefaultNavValue. It is NOT 0: answering 0 here
        // would be the runner deciding the column's value rather than leaving it to BC, and
        // the two are indistinguishable downstream on a column whose default happens to be 0.
        Assert.Equal(-1, Parse("").RequiredTestIsolationOrdinal);
        Assert.Equal(-1, Parse("TestIsolation=\"\"").RequiredTestIsolationOrdinal);

        // The absent case is the Subtype = Test one — the compiler omits the attribute for a
        // test codeunit and supplies Disabled for everything else — so this is the branch
        // 32 of Base Application's 1,690 codeunit documents take.
        Assert.NotEqual(
            Parse("TestIsolation=\"Disabled\"").RequiredTestIsolationOrdinal,
            Parse("").RequiredTestIsolationOrdinal);
    }

    [Fact]
    public void TestIsolationTheColumnDoesNotName_IsRefused_NotDefaulted()
    {
        // A member the column has no ordinal for is refused rather than silently written as 0,
        // which would read exactly like a codeunit that declares nothing. Same rule
        // ResolveCodeunitSubtypeOrdinal applies to SubType (#3080).
        //
        // Unreachable from AL: the compiler only ever writes members this column names. It
        // becomes reachable the day BC adds an isolation mode, and then the runner says so
        // instead of quietly answering None.
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => Parse("TestIsolation=\"PerTest\""));
        Assert.Contains("PerTest", ex.Message);
        Assert.Contains("60963", ex.Message);
    }

    [Fact]
    public void NoIsolationOrdinalsAvailable_LeavesTheColumnToBcsDefault_RatherThanThrowing()
    {
        // An artifact whose CodeUnit Metadata has no RequiredTestIsolation column at all: the
        // value has nowhere to go, and the other three columns are still answerable. Refusing
        // here would cost the whole row for a column that does not exist.
        var parsed = RecordPatches.ParseCodeunitMetadataDocument(
            Document("TestIsolation=\"Codeunit\" ALNamespace=\"A.B\""), 60963, isolationOrdinals: null);
        Assert.Equal(-1, parsed.RequiredTestIsolationOrdinal);
        Assert.Equal("A.B", parsed.AlNamespace);
    }

    // ── The two permission columns: BC's own spelling, and they never bleed into each other ──

    [Fact]
    public void ExecuteMask_SpellsX_AndTheColumnTheCodeunitDidNotDeclareStaysEmpty()
    {
        // 16 is PermissionMask.Execute, the only kind AL accepts on a codeunit (AL0195), and
        // the only value observed on Base Application. The empty sibling is the half that
        // proves the two attributes are read independently rather than one being echoed.
        var permOnly = Parse("InherentPermissions=\"16\"");
        Assert.Equal("X", permOnly.InherentPermissions);
        Assert.Equal("", permOnly.InherentEntitlements);

        var entOnly = Parse("InherentEntitlements=\"16\"");
        Assert.Equal("X", entOnly.InherentEntitlements);
        Assert.Equal("", entOnly.InherentPermissions);
    }

    [Fact]
    public void EveryDirectPermissionBit_GetsItsOwnLetter_InBcsOwnOrder()
    {
        // R,I,M,D,X at bits 0..4 — MetadataDataProvider.permissions, decoded from Ncl.dll.
        // Unreachable from AL on a codeunit, and the reason to pin it anyway is that the same
        // two columns exist on Table and Page Metadata, where the mask is not restricted: a
        // half-implementation here is what the next conversion would copy.
        Assert.Equal("R", Parse("InherentPermissions=\"1\"").InherentPermissions);
        Assert.Equal("I", Parse("InherentPermissions=\"2\"").InherentPermissions);
        Assert.Equal("M", Parse("InherentPermissions=\"4\"").InherentPermissions);
        Assert.Equal("D", Parse("InherentPermissions=\"8\"").InherentPermissions);
        Assert.Equal("X", Parse("InherentPermissions=\"16\"").InherentPermissions);

        // Combined, in bit order and not in the order the bits were set.
        Assert.Equal("RIMD", Parse("InherentPermissions=\"15\"").InherentPermissions);
        Assert.Equal("RIMDX", Parse("InherentPermissions=\"31\"").InherentPermissions);
    }

    [Fact]
    public void IndirectPermissionBits_LowercaseTheirLetter_AndTheDirectBitWins()
    {
        // Bits 5..9 are the indirect half; BC emits permissions[n] + 32, the lowercase letter.
        Assert.Equal("r", Parse("InherentPermissions=\"32\"").InherentPermissions);
        Assert.Equal("x", Parse("InherentPermissions=\"512\"").InherentPermissions);
        Assert.Equal("ri", Parse("InherentPermissions=\"96\"").InherentPermissions);

        // With both bits set for one permission, the direct one is the answer — one character
        // per permission, never two. BC reaches the same result by sizing its buffer with
        // PopCount((num >> 5) | (num & 0x1F)), which cannot hold both either.
        Assert.Equal("R", Parse("InherentPermissions=\"33\"").InherentPermissions);
        Assert.Equal("Ri", Parse("InherentPermissions=\"65\"").InherentPermissions);
    }

    [Fact]
    public void NoMask_AndAnExplicitZeroMask_BothSpellTheEmptyString()
    {
        // PermissionMask.None is NavText.Empty in BC's own CreatePermissionMaskString, and an
        // absent attribute is the same answer — which is correct, because a codeunit declaring
        // no permission and one declaring an empty mask are the same codeunit.
        Assert.Equal("", Parse("").InherentPermissions);
        Assert.Equal("", Parse("InherentPermissions=\"0\"").InherentPermissions);
        Assert.Equal("", Parse("InherentPermissions=\"None\"").InherentPermissions);
    }

    [Fact]
    public void MemberNameSpelledMask_IsAcceptedAlongsideTheNumericOne()
    {
        // BC's own MetaCodeunit ctor parses this attribute with Enum.Parse(PermissionMask, …),
        // which takes both a numeric string and a member name. The compiler writes the number,
        // so the name form is unreachable from AL — accepted here because BC accepts it.
        Assert.Equal("X", Parse("InherentPermissions=\"Execute\"").InherentPermissions);
        Assert.Equal("RI", Parse("InherentPermissions=\"Read, Insert\"").InherentPermissions);
        Assert.Equal("r", Parse("InherentPermissions=\"IndirectRead\"").InherentPermissions);
    }

    [Fact]
    public void MaskTheRunnerCannotSpell_IsRefused_NotAnsweredAsEmpty()
    {
        // An empty answer would be indistinguishable from a codeunit declaring no permission,
        // so a mask that is neither numeric nor a name this file knows is refused instead.
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => Parse("InherentPermissions=\"Superuser\""));
        Assert.Contains("Superuser", ex.Message);
        Assert.Contains("InherentPermissions", ex.Message);
    }

    // ── AL Namespace: stated-empty and absent are the same answer, and that is deliberate ──

    [Fact]
    public void StatedNamespace_IsReportedInFull_NotOneSegmentOfIt()
    {
        Assert.Equal(
            "ALLanguage.Coverage.MetadataProbes",
            Parse("ALNamespace=\"ALLanguage.Coverage.MetadataProbes\"").AlNamespace);
    }

    [Fact]
    public void AbsentAndStatedEmptyNamespace_BothAnswerTheEmptyString()
    {
        // BC emits ALNamespace="" for an object whose file states no namespace, so on any real
        // bundle the two are the same case and no AL test can separate them. Pinned here
        // because the runner must not treat an absent attribute as a reason to answer
        // something other than empty.
        Assert.Equal("", Parse("ALNamespace=\"\"").AlNamespace);
        Assert.Equal("", Parse("").AlNamespace);
    }

    // ── A document that is not a document ───────────────────────────────────────────────────

    [Fact]
    public void MalformedDocument_IsRefused_RatherThanFallingBackToTheOldDefaults()
    {
        // A registered but unparseable document means BC's emitter changed shape. Answering
        // the pre-#3606 defaults would hide that behind a green run, which is what
        // .claude/rules/loud-failures.md exists to prevent.
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => RecordPatches.ParseCodeunitMetadataDocument(
                "<CodeUnit ID=\"60963\"", codeunitId: 60963, IsolationMap()));
        Assert.Contains("60963", ex.Message);
        Assert.Contains("well-formed", ex.Message);
    }

    [Fact]
    public void UnregisteredCodeunit_ReadsAsNoDocument_RatherThanAnEmptyOne()
    {
        // Every codeunit in a precompiled dependency takes this branch: the .app ships no
        // metadata XML, so there is nothing to read and the four columns keep BC's defaults.
        // null and a document whose four attributes are all absent are different states — the
        // second is a document that says the values are empty, the first is no answer at all.
        Assert.Null(RecordPatches.TryReadCodeunitMetadataDocument(
            codeunitId: 2147483600, IsolationMap()));
    }
}
