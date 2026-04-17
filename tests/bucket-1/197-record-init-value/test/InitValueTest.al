/// Tests for Record.Init() applying field InitValue declarations.
/// Covers: Integer InitValue, Boolean InitValue, Decimal InitValue,
/// fields without InitValue (type default), PK preservation, and reinit.
codeunit 100401 "InitValue Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "InitValue Src";

    // ── InitValue applied ─────────────────────────────────────────────────────

    [Test]
    procedure Init_IntegerInitValue_AppliedCorrectly()
    begin
        // [GIVEN] Priority has InitValue = 5
        // [WHEN] Rec.Init() is called
        // [THEN] Priority = 5 (not 0)
        Assert.AreEqual(5, Src.GetPriorityAfterInit(),
            'Priority must equal InitValue = 5 after Init()');
    end;

    [Test]
    procedure Init_BooleanInitValue_AppliedCorrectly()
    begin
        // [GIVEN] IsActive has InitValue = true
        // [WHEN] Rec.Init() is called
        // [THEN] IsActive = true (not false)
        Assert.IsTrue(Src.GetIsActiveAfterInit(),
            'IsActive must equal InitValue = true after Init()');
    end;

    [Test]
    procedure Init_DecimalInitValue_AppliedCorrectly()
    begin
        // [GIVEN] Score has InitValue = 9.99
        // [WHEN] Rec.Init() is called
        // [THEN] Score = 9.99 (not 0)
        Assert.AreEqual(9.99, Src.GetScoreAfterInit(),
            'Score must equal InitValue = 9.99 after Init()');
    end;

    // ── No InitValue → type defaults ──────────────────────────────────────────

    [Test]
    procedure Init_CodeFieldNoInitValue_EmptyString()
    begin
        // [GIVEN] "No." has no InitValue (Code[20])
        // [WHEN] Rec.Init() is called on a fresh record
        // [THEN] "No." = '' (type default for Code)
        Assert.AreEqual('', Src.GetNoAfterInit(),
            'Code field with no InitValue must be empty after Init()');
    end;

    [Test]
    procedure Init_TextFieldNoInitValue_EmptyString()
    begin
        // [GIVEN] Description has no InitValue (Text[100])
        // [WHEN] Rec.Init() is called
        // [THEN] Description = '' (type default for Text)
        Assert.AreEqual('', Src.GetDescAfterInit(),
            'Text field with no InitValue must be empty after Init()');
    end;

    // ── Reinit overwrites previous value ─────────────────────────────────────

    [Test]
    procedure Init_OverwritesPreviousValue_WithInitValue()
    begin
        // [GIVEN] Priority was set to 99
        // [WHEN] Rec.Init() is called
        // [THEN] Priority is reset to InitValue = 5 (not 99)
        Assert.AreEqual(5, Src.GetPriorityAfterReinit(),
            'Priority must be reset to InitValue = 5 when Init() is called after assigning 99');
    end;

    // ── PK field preserved ────────────────────────────────────────────────────

    [Test]
    procedure Init_PreservesPKField()
    begin
        // [GIVEN] "No." (PK) was set to 'TEST001'
        // [WHEN] Rec.Init() is called
        // [THEN] "No." is still 'TEST001' — PK fields survive Init()
        Assert.AreEqual('TEST001', Src.GetNoPkPreserved('TEST001'),
            'PK field "No." must be preserved across Init()');
    end;
}
