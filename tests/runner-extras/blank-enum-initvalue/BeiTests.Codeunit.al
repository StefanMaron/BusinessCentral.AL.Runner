// Issue #1674: enum value named " " with InitValue = " " — before the fix,
// every Insert of such a table threw NavNCLInvalidOptionStringException
// ('" "' is not an option. The existing options are:) from
// NCLMetaField.get_InitValue via MarkAllFieldsAsChanged, killing the bundle
// at the install trigger. These tests only being REACHED already proves the
// install-path Insert (BEI Installer) survived; the assertions then pin the
// concrete evaluated values so a silent default-0 fallback cannot pass the
// named-ordinal check.
codeunit 64206 "BEI Blank Enum Init Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "BEI Assert";

    [Test]
    procedure InstallInsertSucceededAndBlankEnumReadsBackAsOrdinalZero()
    var
        BeiSetup: Record "BEI Setup";
    begin
        // [THEN] The row the install trigger inserted (blank PK, exactly as in
        // the #1674 repro) exists — on main this codeunit never ran at all.
        Assert.IsTrue(BeiSetup.Get(''), 'the install-trigger-inserted row must exist');
        Assert.AreEqual(0, BeiSetup."Logging Type".AsInteger(),
            'InitValue = " " must initialize the enum field to the blank ordinal-0 value');
        Assert.IsTrue(BeiSetup."Logging Type" = BeiSetup."Logging Type"::" ",
            'the field must equal the blank-named enum value');
    end;

    [Test]
    procedure InTestInsertInitializesBlankEnumToOrdinalZero()
    var
        BeiSetup: Record "BEI Setup";
    begin
        // [WHEN] The same Init+Insert path runs inside a test.
        BeiSetup.Init();
        BeiSetup."Primary Key" := 'T1';
        BeiSetup.Insert(false);

        // [THEN] The row round-trips with the blank ordinal-0 value.
        BeiSetup.Get('T1');
        Assert.AreEqual(0, BeiSetup."Logging Type".AsInteger(),
            'inserted row must read back the blank ordinal-0 enum value');
    end;

    [Test]
    procedure NamedInitValueOnSparseEnumEvaluatesToTrueOrdinal()
    var
        BeiMixed: Record "BEI Mixed";
    begin
        // [WHEN] Init applies InitValue to every field.
        BeiMixed.Init();

        // [THEN] Blank InitValue -> 0; NAMED InitValue -> the enum's true sparse
        // ordinal 5 (a silent default-0 fallback fails here); Option-field blank
        // InitValue (the pre-fix healthy path) -> 0.
        Assert.AreEqual(0, BeiMixed."Blank Init".AsInteger(),
            'blank InitValue on a mixed enum must initialize to ordinal 0');
        Assert.AreEqual(5, BeiMixed."Named Init".AsInteger(),
            'InitValue = Verbose must evaluate to the sparse ordinal 5, not a default');
        Assert.AreEqual(0, BeiMixed."Option Blank",
            'blank InitValue on a classic Option field must stay at ordinal 0');

        // [THEN] The values survive Insert + Get.
        BeiMixed."Primary Key" := 'M1';
        BeiMixed.Insert(false);
        BeiMixed.Get('M1');
        Assert.AreEqual(5, BeiMixed."Named Init".AsInteger(),
            'the named-InitValue ordinal must round-trip through Insert');
    end;

    [Test]
    procedure EvaluateMatchesBlankMemberAndRejectsNonMember()
    var
        BeiSetup: Record "BEI Setup";
    begin
        // [THEN] The blank member name matches through the SAME evaluator path
        // (NavOptionEvaluator -> GetIndexFromCaption/GetIndexFromOption) that
        // InitValue uses...
        Evaluate(BeiSetup."Logging Type", ' ');
        Assert.AreEqual(0, BeiSetup."Logging Type".AsInteger(),
            'Evaluate('' '') must resolve to the blank ordinal-0 member');

        // [THEN] ...and a non-member is still rejected loudly, proving the
        // matcher is a real matcher and not an always-succeed default.
        asserterror Evaluate(BeiSetup."Logging Type", 'Bogus');
        Assert.ExpectedError('is not an option', GetLastErrorText());
    end;
}
