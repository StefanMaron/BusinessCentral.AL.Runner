/// <summary>
/// #5445: "No Such Enum" and "Another Missing Enum" are declared by no app. --tdd must add an enum of that name (empty),
/// then the values these tests name; an enum that DOES exist keeps its values.
/// </summary>
codeunit 65320 "Missing Enum Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Shared: Enum "Another Missing Enum";

    [Test]
    procedure VariableValues_RunToTheGeneratedEnum()
    var
        K: Enum "No Such Enum";
    begin
        K := K::Open;
        if K <> K::Open then
            Error('Open was not kept');
        K := K::Closed;
        if K = K::Open then
            Error('Closed equals Open');
        if K.AsInteger() <> 1 then
            Error('Closed is %1, expected 1', K.AsInteger());
    end;

    [Test]
    procedure LiteralValue_RunsToTheGeneratedEnum()
    var
        K: Enum "No Such Enum";
    begin
        K := Enum::"No Such Enum"::Pending;
        if K.AsInteger() <> 2 then
            Error('Pending is %1, expected 2', K.AsInteger());
    end;

    [Test]
    procedure GlobalVariable_RunsToTheGeneratedEnum()
    begin
        Shared := Shared::Left;
        if Shared <> Shared::Left then
            Error('Left was not kept');
    end;

    [Test]
    procedure ExistingEnum_KeepsItsRealValues()
    var
        K: Enum "Existing Enum";
    begin
        K := K::Beta;
        if K.AsInteger() <> 1 then
            Error('Beta is %1, expected 1', K.AsInteger());
    end;

    [Test]
    procedure Unrelated_NamesNoStub()
    var
        I: Integer;
    begin
        I := 1;
        if I <> 1 then
            Error('arithmetic');
    end;
}
