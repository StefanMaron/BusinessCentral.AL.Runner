/// <summary>The same missing object used from a second file: one object, shared, never a second one.</summary>
codeunit 65322 "Missing Object Second Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure SameObject_InASecondFile()
    var
        Missing: Codeunit "No Such Codeunit";
        Result: Integer;
    begin
        Result := Missing.Calc(1);
    end;

    [Test]
    procedure DisagreeingShapes_TheFirstFileDecides()
    var
        Missing: Codeunit "No Such Codeunit";
        Result: Integer;
    begin
        Result := Missing.Pick(1);
    end;

    [Test]
    procedure AnotherObject_TakesTheNextId()
    var
        Other: Codeunit "Another Missing Codeunit";
        Result: Decimal;
    begin
        Result := Other.Total(2.5);
    end;
}
