// TestPageNewRecordValidationTests — issue #2551, gap 2.
//
// This is a RUNNER-MECHANISM test, not a claim anyone is being asked to take on faith about BC.
//
// What BC does is settled upstream: corpus codeunit 60653 "NRB Tests"
// (StefanMaron/BusinessCentral.AL.Language.Tests#150) measured on all eight BC legs that
// TestPage.New() through a field(...) SubPageLink runs the stamped field's OnValidate. That
// took a round trip to get right — the first version of the assertion compared against 'True'
// and went red on all eight legs with Actual:<Yes>, which is the same value spelled differently
// — and the merged version is spelling-independent as a result.
//
// This suite exists for the three things that verdict does NOT cover, all of which are
// properties of the RUNNER's own stamping path (MockTestPage.InsertEmptyRow ->
// ValidateStampedFields) rather than of BC:
//
//   1. The submodule pin is behind that corpus commit, so nothing in this repository runs
//      codeunit 60653 yet. Following the precedent CodeunitMetadataVirtualTableTests set: a
//      regression in our own pipeline should fail loudly HERE, without needing the pin bumped
//      first — especially while the catch-up bump (#2808) is in flight on another branch.
//
//   2. The corpus test uses a field(...) link. The runner also validates const(...)- and
//      filter(...)-derived stamps that pass the primary-key gate, and NOTHING pins that arm
//      anywhere. A fix that validated only field(...) stamps would pass upstream and still be
//      wrong.
//
//   3. "New() validates" is the easy half. "New() validates the STAMPED SET, and only that" is
//      the claim worth holding: BC hands ValidateFieldsAsync exactly
//      fieldsInitializedFromFilters, so validating every primary-key field — or every field —
//      would be as wrong as validating nothing. Two of the fixture's tests assert the negative
//      direction, which is what stops the fix from quietly becoming "validate everything".
//
// It also pins CurrFieldNo = 0 during that validate. That is a CHOICE I made by following BC's
// call shape (ValidateFieldsAsync is record-level, like Rec.Validate, which leaves CurrFieldNo
// at 0 — unlike ValueControl.SetValue's page-originated write, #2705), and no corpus test pins
// it in either direction on any BC leg. Recording it means a future measurement that
// contradicts it fails loudly here instead of drifting silently.
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageNewRecordValidationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static readonly string FixtureDir =
        Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TestPageNewRecordValidation");

    [Fact]
    public async Task New_ValidatesTheStampedSet_AndOnlyTheStampedSet()
    {
        var r = await SuiteServer.RunViaServer(FixtureDir);

        Assert.True(r.ExitCode == 0,
            $"every fixture test must pass. exit={r.ExitCode}\n{r}");

        // Positive: both link kinds are stamped AND validated. The const(...) one is the
        // arm no corpus test reaches.
        r.AssertPassed("Codeunit70405.New_FieldLinkedPrimaryKeyField_IsStampedAndValidated");
        r.AssertPassed("Codeunit70405.New_ConstLinkedPrimaryKeyField_IsStampedAndValidated");

        // Negative: a primary-key field no link names, and a field outside the key entirely.
        // These are what make the claim "the stamped set" rather than "everything".
        r.AssertPassed("Codeunit70405.New_PrimaryKeyFieldNoLinkNames_IsNotValidated");
        r.AssertPassed("Codeunit70405.New_FieldOutsideThePrimaryKeyAndOutsideTheLink_IsNotValidated");

        // The unpinned choice, recorded so it cannot drift silently.
        r.AssertPassed("Codeunit70405.New_ValidatesWithCurrFieldNoZero_NotAsAPageWrite");

        r.AssertNoFailures();
    }
}
