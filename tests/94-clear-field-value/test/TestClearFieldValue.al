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
}
