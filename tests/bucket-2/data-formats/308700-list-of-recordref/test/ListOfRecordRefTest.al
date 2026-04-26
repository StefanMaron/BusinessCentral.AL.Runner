codeunit 308701 "List Of RecordRef Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "List Of RecordRef Helper";

    [Test]
    procedure GetRecRef_WithIntegerField_Positive()
    var
        TempTable: Record "List RecordRef Table" temporary;
        Result: Boolean;
    begin
        // [GIVEN] A temp record with Function Id = 99
        TempTable.Init();
        TempTable."Entry No." := 1;
        TempTable."Function Id" := 99;
        TempTable.Insert();

        // [WHEN] Calling GetRecRef with an Integer field as FunctionId (reproduces CS1503 telemetry)
        Result := Helper.CallGetRecRef(TempTable);

        // [THEN] Returns true because 99 > 0
        Assert.IsTrue(Result, 'GetRecRef should return true for FunctionId = 99');
    end;

    [Test]
    procedure GetRecRef_WithZeroIntegerField_Negative()
    var
        TempTable: Record "List RecordRef Table" temporary;
        Result: Boolean;
    begin
        // [GIVEN] A temp record with Function Id = 0
        TempTable.Init();
        TempTable."Entry No." := 2;
        TempTable."Function Id" := 0;
        TempTable.Insert();

        // [WHEN] Calling GetRecRef with zero Integer field
        Result := Helper.CallGetRecRef(TempTable);

        // [THEN] Returns false because 0 is not > 0
        Assert.IsFalse(Result, 'GetRecRef should return false for FunctionId = 0');
    end;

    [Test]
    procedure ListOfRecordRef_Assign_ClearsExisting()
    var
        Refs: List of [RecordRef];
        RecRef: RecordRef;
    begin
        // [GIVEN] A list with one entry
        RecRef.Open(308700);
        Refs.Add(RecRef);
        Assert.AreEqual(1, Refs.Count(), 'List should have 1 element before UpdateList');

        // [WHEN] Assigning a new (empty) list via var parameter
        Helper.UpdateList(Refs);

        // [THEN] The list is now empty — ALAssign replaced the list
        Assert.AreEqual(0, Refs.Count(), 'List should be empty after UpdateList assigns a new list');
    end;
}
