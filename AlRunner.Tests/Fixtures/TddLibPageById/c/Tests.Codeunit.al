codeunit 72870 "TPIC Tests"
{
    Subtype = Test;

    [Test]
    procedure PageOpenedByIdInALibrary_StartsThePagesOnOpenPageOfAnotherBundle()
    var TP: TestPage "TPI Card"; O: Codeunit "TPI Opener";
    begin
        TP.Trap();
        O.OpenById(Page::"TPI Card");
        TP.Close();
    end;

    [Test]
    procedure NoPage_IsNotAnnotated()
    var R: Record "TPI Rec";
    begin
        R.PK := 'A';
        R.Insert();
    end;
}
