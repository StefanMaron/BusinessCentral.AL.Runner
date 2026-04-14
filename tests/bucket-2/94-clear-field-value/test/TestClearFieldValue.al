codeunit 94002 "CFV Clear Field Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ClearTextFieldResetsToEmpty()
    var
        Helper: Codeunit "CFV Clear Helper";
        Rec: Record "CFV Test Record";
    begin
        // [GIVEN] A record with a text field set
        Rec.Id := 1;
        Rec.Name := 'Hello';

        // [WHEN] Clear is called on the field
        Helper.SetAndClearName(Rec, 'World');

        // [THEN] The field is reset to empty
        Assert.AreEqual('', Rec.Name, 'Cleared text field should be empty');
    end;

    [Test]
    procedure ClearDecimalFieldResetsToZero()
    var
        Helper: Codeunit "CFV Clear Helper";
        Rec: Record "CFV Test Record";
    begin
        // [GIVEN] A record with a decimal field set
        Rec.Id := 2;
        Rec.Amount := 99.9;

        // [WHEN] Clear is called on the field
        Helper.SetAndClearAmount(Rec, 50.5);

        // [THEN] The field is reset to zero
        Assert.AreEqual(0, Rec.Amount, 'Cleared decimal field should be zero');
    end;

    [Test]
    procedure ClearFieldNegativeNotOriginalValue()
    var
        Helper: Codeunit "CFV Clear Helper";
        Rec: Record "CFV Test Record";
    begin
        // [NEGATIVE] After Clear, the field should not retain the set value
        Rec.Id := 3;
        Helper.SetAndClearName(Rec, 'ShouldBeGone');
        Assert.AreNotEqual('ShouldBeGone', Rec.Name, 'Cleared field must not retain original value');
    end;

    [Test]
    procedure ClearUninitializedFieldIsNoOp()
    var
        Helper: Codeunit "CFV Clear Helper";
        Rec: Record "CFV Test Record";
    begin
        // [GIVEN] A record where the Name field was never explicitly set
        Rec.Id := 4;

        // [WHEN] Clear is called on the uninitialized field
        Helper.ClearName(Rec);

        // [THEN] No crash, field remains at type default
        Assert.AreEqual('', Rec.Name, 'Clearing uninitialized text field should not crash and remain empty');
    end;

    [Test]
    procedure ClearFieldTwiceIsIdempotent()
    var
        Helper: Codeunit "CFV Clear Helper";
        Rec: Record "CFV Test Record";
    begin
        // [GIVEN] A record with a field set
        Rec.Id := 5;
        Rec.Name := 'DoubleTest';

        // [WHEN] Clear is called twice on the same field
        Helper.ClearName(Rec);
        Helper.ClearName(Rec);

        // [THEN] No crash, field is still at type default
        Assert.AreEqual('', Rec.Name, 'Double clear should be idempotent');
    end;

    [Test]
    procedure ClearFieldDoesNotAffectOtherFields()
    var
        Helper: Codeunit "CFV Clear Helper";
        Rec: Record "CFV Test Record";
    begin
        // [GIVEN] A record with both fields set
        Rec.Id := 6;
        Rec.Name := 'Keep';
        Rec.Amount := 42.5;

        // [WHEN] Clear is called only on the Amount field
        Helper.ClearAmount(Rec);

        // [THEN] The Name field is unaffected
        Assert.AreEqual('Keep', Rec.Name, 'Other text field should be unaffected by clearing Amount');
        Assert.AreEqual(0, Rec.Amount, 'Cleared Amount should be zero');
        Assert.AreEqual(6, Rec.Id, 'Id should be unaffected by clearing Amount');
    end;
}
