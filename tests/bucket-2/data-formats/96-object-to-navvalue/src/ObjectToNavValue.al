table 1260001 "Object NavValue Table"
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
}

codeunit 1260001 "Object NavValue Helper"
{
    procedure GetViaVariantKey(var Rec: Record "Object NavValue Table"; KeyValue: Variant)
    begin
        Rec.Get(KeyValue);
    end;

    procedure ValidateFromVariant(var Rec: Record "Object NavValue Table"; FieldValue: Variant)
    begin
        Rec.Validate("Description", FieldValue);
    end;

    procedure SetRangeFromVariant(var Rec: Record "Object NavValue Table"; Value: Variant)
    begin
        Rec.SetRange("Code", Value);
    end;

    procedure SetRangeFromToVariant(var Rec: Record "Object NavValue Table"; FromVal: Variant; ToVal: Variant)
    begin
        Rec.SetRange("Code", FromVal, ToVal);
    end;

    procedure SetFilterFromVariant(var Rec: Record "Object NavValue Table"; FilterVal: Variant)
    begin
        Rec.SetFilter("Code", '%1', FilterVal);
    end;

    procedure ModifyAllFromVariant(var Rec: Record "Object NavValue Table"; NewVal: Variant)
    begin
        Rec.ModifyAll("Description", NewVal);
    end;
}
