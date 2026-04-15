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
    procedure CopyRowsLeavesTargetEmpty(): Boolean
    var
        DT: DataTransfer;
        Src: Record "DT Source";
        Tgt: Record "DT Target";
    begin
        Src."Entry No." := 1;
        Src."Name" := 'Test';
        Src.Insert();

        DT.SetTables(Database::"DT Source", Database::"DT Target");
        DT.AddFieldValue(2, 2);
        DT.CopyRows();

        exit(Tgt.IsEmpty());
    end;

    procedure CopyFieldsLeavesTargetEmpty(): Boolean
    var
        DT: DataTransfer;
        Src: Record "DT Source";
        Tgt: Record "DT Target";
    begin
        Src."Entry No." := 1;
        Src."Name" := 'Test';
        Src.Insert();

        DT.SetTables(Database::"DT Source", Database::"DT Target");
        DT.AddFieldValue(2, 2);
        DT.CopyFields();

        exit(Tgt.IsEmpty());
    end;

    procedure AddConstantValueNoError(): Boolean
    var
        DT: DataTransfer;
        Tgt: Record "DT Target";
    begin
        DT.SetTables(Database::"DT Source", Database::"DT Target");
        DT.AddConstantValue('test', 2);
        DT.CopyRows();
        exit(Tgt.IsEmpty());
    end;
}
