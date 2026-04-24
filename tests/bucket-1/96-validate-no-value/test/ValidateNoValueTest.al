codeunit 305002 "Validate No Value Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";

    [Test]
    procedure ValidateNoValue_InOnInsert_FiresTrigger()
    var
        Rec: Record "Validate No Value Table";
    begin
        // [GIVEN] A record with Quantity set directly (bypassing trigger)
        Rec.Init();
        Rec."Entry No." := 1;
        Rec.Quantity := 5;

        // [WHEN] Insert fires OnInsert which calls Validate(Quantity) without a value
        Rec.Insert(true);

        // [THEN] The OnValidate trigger fires and sets Validated Qty = Quantity * 2
        Assert.AreEqual(10, Rec."Validated Qty", 'OnValidate should fire via Validate(Quantity) in OnInsert and set Validated Qty to Quantity * 2');
    end;

    [Test]
    procedure ValidateWithValue_SetsAndFiresTrigger()
    var
        Rec: Record "Validate No Value Table";
    begin
        // [GIVEN] A record with no Quantity
        Rec.Init();
        Rec."Entry No." := 2;

        // [WHEN] Validate(Quantity, 7) is called with a value
        Rec.Validate(Quantity, 7);

        // [THEN] Quantity is set to 7 and OnValidate fires
        Assert.AreEqual(7, Rec.Quantity, 'Quantity should be set to 7');
        Assert.AreEqual(14, Rec."Validated Qty", 'OnValidate should fire and set Validated Qty to 14');
    end;
}
