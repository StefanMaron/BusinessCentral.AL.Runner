/// <summary>
/// #5445: "No Such Table" and "Another Missing Table" are declared by no app. --tdd must add a table of that name
/// (one placeholder primary key field), then the fields these tests assign; a table that DOES exist keeps its shape.
/// </summary>
codeunit 65320 "Missing Table Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Shared: Record "Another Missing Table";

    [Test]
    procedure InitAndInsert_RunToTheGeneratedTable()
    var
        T: Record "No Such Table";
    begin
        T.Init();
        T.Insert();
    end;

    [Test]
    procedure Get_FindsTheInsertedRow()
    var
        T: Record "No Such Table";
    begin
        T.Insert();
        Clear(T);
        if not T.FindFirst() then
            Error('the row was not found');
        if not T.Get(T."TDD Key") then
            Error('the row was not found by its key');
    end;

    [Test]
    procedure AssignedField_RoundTripsThroughTheTable()
    var
        T: Record "No Such Table";
    begin
        T."Amount" := 5;
        T.Insert();
        Clear(T);
        T."Amount" := 5;
        T.Insert();
        T.Reset();
        T.SetRange("Amount", 5);
        if T.Count() <> 2 then
            Error('Expected 2 rows, got %1', T.Count());
    end;

    [Test]
    procedure GlobalVariable_RunsToTheGeneratedTable()
    begin
        Shared."Quantity" := 3;
        Shared.Insert();
    end;

    [Test]
    procedure ExistingTable_KeepsItsRealShape()
    var
        T: Record "Existing Table";
    begin
        T."Code" := 9;
        T."Label" := true;
        T.Insert();
        Clear(T);
        T.Get(9);
        if not T."Label" then
            Error('the real Label field was not kept');
    end;

    [Test]
    procedure Unrelated_NamesNoStub()
    var
        Value: Integer;
    begin
        Value := 1;
        if Value <> 1 then
            Error('arithmetic broke');
    end;
}
