// CodeunitMetadataDocumentColumnTests — how three CodeUnit Metadata (2000000137) columns are
// read out of BC's own emitted metadata document, and what happens to a document that states
// something the runner cannot spell (#3606).
//
// WHY THIS IS A RUNNER-SIDE MECHANISM TEST AND NOT (ONLY) AN AL BUNDLE
// -------------------------------------------------------------------
// The BC-behaviour claims are upstream, in corpus PR 296 — an X-declaring codeunit reporting
// 'X' while its sibling column stays empty, and a namespaced codeunit reporting its full
// dotted namespace. Both are green on eight real service tiers. Nothing here restates them.
//
// What this file pins is what no AL assertion can reach. An AL test observes only the
// documents the AL compiler chose to emit for the bundle it ran on, so it cannot present:
//
//   * a permission mask other than X — AL0195 rejects every other kind on a codeunit, so the
//     R/I/M/D letters and the lowercase indirect half are unreachable from AL even though the
//     same two columns exist on Table and Page Metadata where the mask is not restricted;
//   * a malformed document, which is a BC emitter shape change rather than anything AL can
//     declare;
//   * an ABSENT ALNamespace attribute as distinct from one stated as the empty string. BC
//     emits ALNamespace="" for an un-namespaced object, so the two are the same answer on any
//     real bundle and a test written against one cannot tell which the runner read.
//
// NO RequiredTestIsolation HERE, AND THAT IS THE POINT
// ---------------------------------------------------
// An earlier draft of this file read that column out of the document's TestIsolation
// attribute and pinned the mapping. Corpus PR 296 put the claim in front of eight cloud legs
// and every one refuted it: a real tier answers None for EVERY codeunit, including one
// declaring TestIsolation = Disabled. The conversion and its tests were removed rather than
// adjusted, and the column went back to BC's own default, which is the faithful answer.
// docs/codeunit-metadata-from-bc.md#requiredtestisolation has the tier evidence and the
// mechanism in Ncl.dll.
//
// The permission spelling asserted below is BC's own, read out of Ncl.dll:
// MetadataDataProvider.permissions decodes to R,I,M,D,X and CreatePermissionMaskString
// lowercases the letter for the indirect bit at n+5. See
// AlRunner/Patches/RecordPatches.CodeunitMetadataFromBcDocument.cs.

using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class CodeunitMetadataDocumentColumnTests
{
    /// <summary>A document with exactly the attributes named, in BC's own emitted shape.</summary>
    private static string Document(string attributes) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + $"<CodeUnit MetadataVersion=\"130000\" ID=\"60963\" Name=\"ALT Codeunit Meta Probe\" {attributes} "
        + "EventSubscriberInstance=\"StaticAutomatic\" SingleInstance=\"0\" "
        + "xmlns=\"urn:schemas-microsoft-com:dynamics:NAV:MetaObjects\" />";

    private static RecordPatches.BcCodeunitDocumentValues Parse(string attributes)
        => RecordPatches.ParseCodeunitMetadataDocument(Document(attributes), codeunitId: 60963);

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
                "<CodeUnit ID=\"60963\"", codeunitId: 60963));
        Assert.Contains("60963", ex.Message);
        Assert.Contains("well-formed", ex.Message);
    }

    [Fact]
    public void UnregisteredCodeunit_ReadsAsNoDocument_RatherThanAnEmptyOne()
    {
        // Every codeunit in a precompiled dependency takes this branch: the .app ships no
        // metadata XML, so there is nothing to read and the three columns keep BC's defaults.
        // null and a document whose three attributes are all absent are different states — the
        // second is a document that says the values are empty, the first is no answer at all.
        Assert.Null(RecordPatches.TryReadCodeunitMetadataDocument(codeunitId: 2147483600));
    }
}
