/// <summary>What the existing member generation cannot add, so the table is generated and the file is FAILED
/// naming the member: a field that is only read, and a Text assignment (no length to infer).</summary>
codeunit 65322 "Missing Table Refused"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure ReadOfAFieldNothingAssigns_IsRefused()
    var
        T: Record "Refused Table";
        Value: Integer;
    begin
        Value := T."Qty";
    end;

    [Test]
    procedure TextAssignment_IsRefused()
    var
        T: Record "Refused Table";
    begin
        T."Description" := 'abc';
    end;
}
