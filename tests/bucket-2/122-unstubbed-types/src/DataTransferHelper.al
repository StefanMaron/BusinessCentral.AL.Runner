table 59810 "DT Source"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Name"; Text[100]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

table 59811 "DT Target"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Name"; Text[100]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

codeunit 59823 "DataTransfer Helper"
{
    procedure CopyRowsNoError()
    var
        DT: DataTransfer;
    begin
        DT.SetTables(Database::"DT Source", Database::"DT Target");
        DT.AddFieldValue(2, 2);
        DT.CopyRows();
    end;

    procedure CopyFieldsNoError()
    var
        DT: DataTransfer;
    begin
        DT.SetTables(Database::"DT Source", Database::"DT Target");
        DT.AddFieldValue(2, 2);
        DT.CopyFields();
    end;

    procedure AddConstantValueNoError()
    var
        DT: DataTransfer;
    begin
        DT.SetTables(Database::"DT Source", Database::"DT Target");
        DT.AddConstantValue('test', 2);
        DT.CopyRows();
    end;
}
