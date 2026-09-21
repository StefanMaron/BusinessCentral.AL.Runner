// CodeunitMetadataTestTypeOrdinalTests — the RESOLVER behind CodeUnit Metadata's TestType
// column (field 9), driven with an option string the test chooses.
//
// WHAT THIS PINS, AND WHAT IT DELIBERATELY DOES NOT
//   The BC claim — "a Subtype = Test codeunit reports TestType::UnitTest" — belongs upstream
//   and is pinned there by Record_CodeunitMetadata_Get_TestCodeunit_ReportsTestTypeUnitTest
//   (corpus PR 394), which reads it off a real service tier. Repeating that assertion here
//   would only prove the runner agrees with itself.
//
//   What is runner-side, and only testable here, is the RESOLUTION: that the ordinal comes
//   from this column's own option string by MEMBER NAME rather than from a hardcoded number,
//   and that a column whose members BC reorders or renames is answered correctly or refused
//   rather than answered wrongly. Those are the cases no single artifact can present, because
//   every provisioned artifact ships the same option string.
//
// WHY IT IS A SEPARATE FILE FROM CodeunitMetadataDocumentColumnTests
//   That file tests the DOCUMENT parse. TestType is never read from the document — BC emits
//   the attribute 0 times and derives the value instead — so this column shares no code with
//   it. See AlRunner/Patches/RecordPatches.CodeunitMetadataVirtualTable.cs.

using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class CodeunitMetadataTestTypeOrdinalTests
{
    /// <summary>The option string the platform package declares for field 9, as
    /// <c>CodeUnitMetadata.Table.al</c> states it. Every assertion that uses this is about
    /// the shipped shape; the ones that pass a different string are about the resolution.</summary>
    private const string ShippedOptionString = "None,UnitTest,IntegrationTest,Uncategorized,AITest";

    private static int Resolve(string? declaredSubtype, string optionString = ShippedOptionString)
        => RecordPatches.ResolveCodeunitTestTypeOrdinal(
            RecordPatches.BuildMetadataOptionOrdinals(optionString, bcRuntimeEnum: null),
            optionString, declaredSubtype, codeunitId: 60963);

    [Fact]
    public void TestSubtype_ResolvesUnitTest_AndEverythingElseResolvesNone()
    {
        // The derivation has exactly one input — the declared Subtype — so the whole rule is
        // these two rows. UnitTest is ordinal 1 and None is 0 in the shipped option string.
        Assert.Equal(1, Resolve("Test"));

        // Every other subtype AL accepts, plus the codeunit that declares none. These are the
        // rows that would break if the derivation widened past Subtype = Test.
        Assert.Equal(0, Resolve(null));
        Assert.Equal(0, Resolve(""));
        Assert.Equal(0, Resolve("Normal"));
        Assert.Equal(0, Resolve("TestRunner"));
        Assert.Equal(0, Resolve("Upgrade"));
        Assert.Equal(0, Resolve("Install"));
    }

    [Fact]
    public void TestRunner_ResolvesNone_NotUnitTest()
    {
        // Called out separately because it is the row a prefix or "contains" match gets wrong:
        // "TestRunner" starts with "Test", and BC's condition is equality on the subtype, not
        // a family test. A resolver matching loosely would answer 1 here.
        Assert.Equal(0, Resolve("TestRunner"));
        Assert.NotEqual(Resolve("Test"), Resolve("TestRunner"));
    }

    [Fact]
    public void TheSubtypeIsMatchedCaseInsensitively_TheWayAlSpellsIt()
    {
        // Both row sources carry the AL author's own spelling — the parser reads
        // `Subtype = Test;` from source and BcAppSymbolCache reads "Subtype": "Test" from
        // SymbolReference.json — and AL is not case-sensitive about it.
        Assert.Equal(1, Resolve("test"));
        Assert.Equal(1, Resolve("TEST"));
    }

    [Fact]
    public void TheOrdinalComesFromTheColumnsOwnOptionString_NotFromAHardcodedNumber()
    {
        // The case no artifact can present: a column whose members are ordered differently.
        // A resolver returning a literal 1 for a test codeunit passes the shipped-shape
        // assertions above and fails here, which is the whole point of resolving by name.
        Assert.Equal(3, Resolve("Test", "None,IntegrationTest,Uncategorized,UnitTest,AITest"));

        // ...and the default member moves with it too.
        Assert.Equal(2, Resolve("Normal", "UnitTest,AITest,None,IntegrationTest"));
        Assert.Equal(0, Resolve("Test", "UnitTest,AITest,None,IntegrationTest"));
    }

    [Fact]
    public void AColumnMissingTheMemberItResolvesTo_IsRefusedRatherThanDefaulted()
    {
        // Answering 0 on a miss would be indistinguishable from the correct answer for every
        // non-test codeunit, so the one case a refusal exists for is the one case it would
        // stay silent on (#3080). Both directions are refused: the derived member missing,
        // and the default member missing.
        var derivedMissing = Assert.Throws<RunnerOutOfScopeException>(
            () => Resolve("Test", "None,IntegrationTest,Uncategorized,AITest"));
        Assert.Contains("UnitTest", derivedMissing.Message);
        Assert.Contains("None,IntegrationTest,Uncategorized,AITest", derivedMissing.Message);

        var defaultMissing = Assert.Throws<RunnerOutOfScopeException>(
            () => Resolve("Normal", "UnitTest,IntegrationTest"));
        Assert.Contains("None", defaultMissing.Message);

        // The message names the codeunit, so a refusal reaching a developer says which row
        // stopped the table rather than only which column.
        Assert.Contains("60963", derivedMissing.Message);
    }

    [Fact]
    public void TheRefusalSaysWhyTheDerivedMemberWasLookedUp_OnlyWhenItWasDerived()
    {
        // A refusal for a test codeunit explains the derivation, because a reader seeing
        // "UnitTest" for a codeunit that declared no such thing needs to know where it came
        // from. A refusal for any other codeunit must NOT claim a derivation it never did.
        var derived = Assert.Throws<RunnerOutOfScopeException>(
            () => Resolve("Test", "None,IntegrationTest"));
        Assert.Contains("derives", derived.Message);

        var notDerived = Assert.Throws<RunnerOutOfScopeException>(
            () => Resolve("Normal", "UnitTest,IntegrationTest"));
        Assert.DoesNotContain("derives", notDerived.Message);
    }
}
