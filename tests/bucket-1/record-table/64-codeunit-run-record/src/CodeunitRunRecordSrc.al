table 57900 "CRR Order"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Status; Code[20]) { }
        field(3; Amount; Decimal) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// <summary>
/// Codeunit with TableNo — updates Status and doubles Amount in OnRun.
/// Proves that Codeunit.Run(ID, rec) passes the record and modifications
/// are written back via Modify().
/// </summary>
codeunit 57901 "CRR Process Order"
{
    TableNo = "CRR Order";

    trigger OnRun()
    begin
        Rec.Status := 'DONE';
        Rec.Amount := Rec.Amount * 2;
        Rec.Modify();
    end;
}

/// <summary>
/// Codeunit with TableNo that raises an error — used to prove that
/// Codeunit.Run returns false when an error occurs.
/// </summary>
codeunit 57902 "CRR Failing Order"
{
    TableNo = "CRR Order";

    trigger OnRun()
    begin
        Error('deliberate failure');
    end;
}
