codeunit 299002 "BVR Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "BVR Helper";

    // -------------------------------------------------------------------------
    // Positive: pass a Record to a "var Variant" parameter.
    // Before the fix this crashes with:
    //   Object of type 'AlRunner.Runtime.MockRecordHandle' cannot be converted
    //   to type 'Microsoft.Dynamics.Nav.Runtime.ByRef<AlRunner.Runtime.MockVariant>'
    // -------------------------------------------------------------------------

    [Test]
    procedure PassRecordToVarVariant_IsRecordTrue()
    var
        Item: Record "BVR Item";
        V: Variant;
    begin
        // [GIVEN] A record with known data
        Item."No." := 'REC-001';
        Item.Description := 'TestItem';
        Item.Quantity := 7;
        Item.Insert();

        // [WHEN] Record is assigned to a Variant and then passed to var Variant param
        V := Item;
        Helper.StoreInVariant(V);

        // [THEN] The Variant still holds a record
        Assert.IsTrue(V.IsRecord(), 'Variant should still hold a record after var Variant call');
    end;

    [Test]
    procedure PassRecordToVarVariant_ExtractDescription()
    var
        Item: Record "BVR Item";
        V: Variant;
        Desc: Text[100];
    begin
        // [GIVEN] A record with a Description
        Item."No." := 'REC-002';
        Item.Description := 'Hello Runner';
        Item.Quantity := 3;
        Item.Insert();

        // [WHEN] Record is passed via var Variant and Description is extracted inside the procedure
        V := Item;
        Desc := Helper.GetDescriptionFromVarVariant(V);

        // [THEN] Description is correctly retrieved — proves mock received the actual record
        Assert.AreEqual('Hello Runner', Desc, 'Description should be extractable from var Variant holding a record');
    end;

    // -------------------------------------------------------------------------
    // Positive: pass non-Record types through var Variant (proves the path is
    // general and not Record-specific). Write-back from inside the procedure.
    // -------------------------------------------------------------------------

    [Test]
    procedure PassIntegerThroughVarVariant_WriteBack()
    var
        V: Variant;
    begin
        // [GIVEN] A Variant holding an initial integer
        V := 0;

        // [WHEN] Procedure writes a specific integer via var Variant
        Helper.SetVariantToInteger(V, 42);

        // [THEN] The Variant now holds the new integer value (write-back worked)
        Assert.AreEqual('42', Format(V), 'var Variant write-back should update integer to 42');
    end;

    [Test]
    procedure PassTextThroughVarVariant_WriteBack()
    var
        V: Variant;
    begin
        // [GIVEN] A Variant holding an initial text
        V := 'initial';

        // [WHEN] Procedure writes a new text via var Variant
        Helper.SetVariantToText(V, 'updated');

        // [THEN] The Variant now holds the updated text (write-back worked)
        Assert.AreEqual('updated', Format(V), 'var Variant write-back should update text to updated');
    end;

    // -------------------------------------------------------------------------
    // Negative: passing Record to var Variant — the record fields must match
    // what was originally inserted. A mock that ignores input would fail this.
    // -------------------------------------------------------------------------

    [Test]
    procedure PassRecordToVarVariant_WrongDescriptionFails()
    var
        Item: Record "BVR Item";
        V: Variant;
        Desc: Text[100];
    begin
        // [GIVEN] A record with a DIFFERENT description
        Item."No." := 'REC-003';
        Item.Description := 'SpecificValue';
        Item.Insert();

        V := Item;
        Desc := Helper.GetDescriptionFromVarVariant(V);

        // [THEN] A mock that always returns '' or 'NOT-A-RECORD' would fail this assertion
        Assert.AreEqual('SpecificValue', Desc, 'Description must match the specific record inserted');
    end;

    [Test]
    procedure PassNonRecordVariantToVarVariant_IsRecordFalse()
    var
        V: Variant;
        Desc: Text[100];
    begin
        // [GIVEN] A Variant holding text, NOT a record
        V := 'just text';

        // [WHEN/THEN] GetDescriptionFromVarVariant returns the sentinel 'NOT-A-RECORD'
        Desc := Helper.GetDescriptionFromVarVariant(V);
        Assert.AreEqual('NOT-A-RECORD', Desc, 'Non-record Variant should return NOT-A-RECORD sentinel');
    end;
}
