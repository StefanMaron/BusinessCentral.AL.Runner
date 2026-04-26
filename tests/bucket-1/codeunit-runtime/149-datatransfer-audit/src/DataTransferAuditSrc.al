table 59560 "DTA Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(3; "Created At"; DateTime) { }
        field(4; "Created By"; Text[50]) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Helper codeunit exercising DataTransfer.UpdateAuditFields — the property
/// issue #475 says is not stubbed.
codeunit 59560 "DTA Src"
{
    procedure GetUpdateAuditFieldsDefault(): Boolean
    var
        DT: DataTransfer;
    begin
        exit(DT.UpdateAuditFields);
    end;

    procedure SetAndGetUpdateAuditFields(Val: Boolean): Boolean
    var
        DT: DataTransfer;
    begin
        DT.UpdateAuditFields := Val;
        exit(DT.UpdateAuditFields);
    end;

    procedure CopyFieldsWithAudit()
    var
        DT: DataTransfer;
    begin
        DT.SetTables(Database::"DTA Item", Database::"DTA Item");
        DT.AddFieldValue(1, 1);
        DT.UpdateAuditFields := true;
        DT.CopyFields();
    end;
}
