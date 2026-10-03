/// <summary>
/// A TABLE with the same name as the publisher codeunit and an event of the same name (#5245): a
/// subscriber of the table's event belongs to the table, not to the codeunit, whose own tests must
/// not name the table subscriber's stub.
/// </summary>
table 65208 "Tdd Shape Publisher"
{
    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }

    procedure RaiseCounted()
    begin
        OnCounted();
    end;

    [IntegrationEvent(false, false)]
    local procedure OnCounted()
    begin
    end;
}
