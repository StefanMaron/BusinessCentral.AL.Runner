codeunit 297002 "Variant Clear Tests"
{
    Subtype = Test;

    var
        Helper: Codeunit "Variant Clear Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure Clear_ResetsVariantToDefault()
    var
        V: Variant;
    begin
        // [GIVEN] A variant holding a specific integer value
        Helper.SetIntegerValue(V, 42);
        Assert.IsTrue(V.IsInteger(), 'Variant should hold integer before Clear');

        // [WHEN] Clear is called on the variant
        Clear(V);

        // [THEN] The variant is no longer an integer (it has been reset)
        Assert.IsFalse(V.IsInteger(), 'Variant should not be integer after Clear');
    end;

    [Test]
    procedure Clear_ViaHelper_ResetsVariantToDefault()
    var
        V: Variant;
    begin
        // [GIVEN] A variant holding integer 99
        Helper.SetIntegerValue(V, 99);
        Assert.IsTrue(V.IsInteger(), 'Variant should hold integer before Clear');

        // [WHEN] Clear is called through a helper procedure (var parameter)
        Helper.ClearVariant(V);

        // [THEN] The variant is reset and no longer holds an integer
        Assert.IsFalse(V.IsInteger(), 'Variant should not hold integer after ClearVariant');
    end;

    [Test]
    procedure Clear_AllowsReassignmentAfterClear()
    var
        V: Variant;
        Txt: Text;
    begin
        // [GIVEN] A variant holding integer 7
        Helper.SetIntegerValue(V, 7);
        Assert.IsTrue(V.IsInteger(), 'Variant should hold integer initially');

        // [WHEN] Cleared and then reassigned a text value
        Clear(V);
        Txt := 'hello';
        V := Txt;

        // [THEN] The variant now holds text
        Assert.IsTrue(V.IsText(), 'Variant should hold text after reassignment');
        Assert.IsFalse(V.IsInteger(), 'Variant should not hold integer after reassignment');
    end;
}
