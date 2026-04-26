/// Table with DataClassification on the table and each field.
table 82000 "AI Test Record"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; Id; Integer)
        {
            DataClassification = SystemMetadata;
        }
        field(2; Name; Text[100])
        {
            DataClassification = EndUserIdentifiableInformation;
        }
        field(3; Notes; Text[250])
        {
            DataClassification = CustomerContent;
            ObsoleteState = Pending;
            ObsoleteReason = 'Use Description instead';
        }
        field(4; Amount; Decimal)
        {
            DataClassification = OrganizationIdentifiableInformation;
        }
        field(5; Active; Boolean)
        {
            DataClassification = ToBeClassified;
        }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// Table extension that adds a field with DataClassification.
tableextension 82000 "AI Test Record Ext" extends "AI Test Record"
{
    fields
    {
        field(10; Code; Code[20])
        {
            DataClassification = ToBeClassified;
        }
    }
}

/// Codeunit that exercises the table with attribute-annotated fields.
codeunit 82000 "AI Attribute Item Lib"
{
    procedure InsertRecord(Id: Integer; Name: Text[100]): Boolean
    var
        Rec: Record "AI Test Record";
    begin
        Rec.Init();
        Rec.Id := Id;
        Rec.Name := Name;
        Rec.Amount := 100;
        Rec.Active := true;
        exit(Rec.Insert());
    end;

    procedure GetName(Id: Integer): Text[100]
    var
        Rec: Record "AI Test Record";
    begin
        Rec.Get(Id);
        exit(Rec.Name);
    end;

    procedure CountRecords(): Integer
    var
        Rec: Record "AI Test Record";
    begin
        exit(Rec.Count());
    end;
}
