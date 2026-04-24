table 1269001 "DTC Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[100]) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 1269001 "DTC Src"
{
    procedure ClearAfterSetup_DoesNotThrow()
    var
        DT: DataTransfer;
    begin
        DT.SetTables(Database::"DTC Item", Database::"DTC Item");
        DT.AddFieldValue(1, 1);
        DT.AddConstantValue('X', 2);
        Clear(DT);
    end;

    procedure ClearThenCopyFields_DoesNotThrow()
    var
        DT: DataTransfer;
    begin
        DT.SetTables(Database::"DTC Item", Database::"DTC Item");
        DT.AddFieldValue(1, 1);
        Clear(DT);
        DT.CopyFields();
    end;

    procedure ClearThenCopyRows_DoesNotThrow()
    var
        DT: DataTransfer;
    begin
        DT.SetTables(Database::"DTC Item", Database::"DTC Item");
        DT.AddFieldValue(1, 1);
        Clear(DT);
        DT.CopyRows();
    end;

    procedure UpdateAuditFieldsSurvivesClear(): Boolean
    var
        DT: DataTransfer;
    begin
        // In BC, Clear resets the DataTransfer including UpdateAuditFields.
        DT.UpdateAuditFields := true;
        Clear(DT);
        exit(DT.UpdateAuditFields);
    end;
}
