codeunit 305001 "FPB FieldNo Test"
{
    Subtype = Test;

    [Test]
    procedure AddFieldNo_Compiles_CountIsOne()
    var
        Src: Codeunit "FPB FieldNo Src";
        Result: Integer;
    begin
        // [SCENARIO] FilterPageBuilder.AddFieldNo compiles and runs when FieldNo (int) is passed
        // [WHEN] We call AddFieldNo with table ID and field number
        Result := Src.AddFieldNoAndGetCount();
        // [THEN] One entry is registered
        Assert.AreEqual(1, Result, 'Count should be 1 after AddFieldNo');
    end;

    var
        Assert: Codeunit "Library Assert";
}
