table 109000 "FieldRef Meta Table"
{
    DataClassification = CustomerContent;
    Caption = 'Field Ref Meta Table';
    fields
    {
        field(1; "Document No."; Code[20])
        {
            Caption = 'Document No.';
        }
        field(2; Description; Text[100])
        {
            Caption = 'Description Caption';
        }
        field(3; Amount; Decimal)
        {
        }
    }
    keys { key(PK; "Document No.") { Clustered = true; } }
}

tableextension 109003 "FieldRef Meta Table Ext" extends "FieldRef Meta Table"
{
    fields
    {
        field(10; "Extended Field"; Text[50])
        {
            Caption = 'Extended Field Caption';
        }
    }
}

codeunit 109001 FieldRefMetaSrc
{
    procedure GetFieldName(TableNo: Integer; FieldNo: Integer): Text
    var
        RecRef: RecordRef;
        FRef: FieldRef;
    begin
        RecRef.Open(TableNo);
        FRef := RecRef.Field(FieldNo);
        exit(FRef.Name);
    end;

    procedure GetFieldCaption(TableNo: Integer; FieldNo: Integer): Text
    var
        RecRef: RecordRef;
        FRef: FieldRef;
    begin
        RecRef.Open(TableNo);
        FRef := RecRef.Field(FieldNo);
        exit(FRef.Caption);
    end;

    procedure GetFieldNameByIndex(TableNo: Integer; FieldIndex: Integer): Text
    var
        RecRef: RecordRef;
        FRef: FieldRef;
    begin
        RecRef.Open(TableNo);
        FRef := RecRef.FieldIndex(FieldIndex);
        exit(FRef.Name);
    end;
}
