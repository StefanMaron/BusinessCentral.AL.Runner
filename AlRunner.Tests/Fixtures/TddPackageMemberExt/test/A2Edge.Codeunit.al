/// <summary>#5491: the other package holds every id of the range but the last (65419).</summary>
codeunit 65401 "Ext Edge Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure EdgeField_TakesTheLastId()
    var
        E: Record "Edge Entry";
    begin
        E.Edged := 7;
        if E.FieldNo(Edged) <> 65419 then
            Error('Expected 65419, got %1', E.FieldNo(Edged));
    end;

    [Test]
    procedure EdgeValue_TakesTheLastOrdinal()
    var
        T: Enum "Edge Tier";
    begin
        T := "Edge Tier"::Edged;
        if T.AsInteger() <> 65419 then
            Error('Expected 65419, got %1', T.AsInteger());
    end;
}
