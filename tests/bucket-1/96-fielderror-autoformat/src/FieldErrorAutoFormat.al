table 1264001 "Field Error AutoFormat"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20])
        {
            Caption = 'Code';
        }
        field(2; "Description"; Text[100])
        {
            Caption = 'Description';
        }
        field(3; "Amount"; Decimal)
        {
            Caption = 'Amount';
        }
    }

    keys
    {
        key(PK; "Code")
        {
            Clustered = true;
        }
    }

    trigger OnInsert()
    begin
        if Description = '' then
            FieldError(Description);
    end;

    procedure ValidateCode()
    begin
        if Code = '' then
            FieldError(Code, 'must not be blank');
    end;
}
