table 60100 FieldErrorTable
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Code"; Code[10])
        {
        }
        field(2; "Name"; Text[100])
        {
            Caption = 'Name';
        }
    }

    keys
    {
        key(PK; "Code")
        {
            Clustered = true;
        }
    }
}

codeunit 60100 FieldErrorHelper
{
    procedure RaiseFieldErrorNoMessage(var Rec: Record FieldErrorTable)
    begin
        Rec.FieldError("Name");
    end;

    procedure RaiseFieldErrorWithMessage(var Rec: Record FieldErrorTable)
    begin
        Rec.FieldError("Name", 'must not be empty');
    end;
}
