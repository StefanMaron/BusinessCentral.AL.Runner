table 313400 "PSTRA Demo Tbl"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Template ID"; Code[20]) { }
        field(2; "Set ID"; Integer) { }
        field(3; "Line No."; Integer) { }
        field(10; "Attribute ID"; Integer) { }
        field(11; Description; Text[50]) { }
    }

    keys
    {
        key(PK; "Template ID", "Set ID", "Line No.") { Clustered = true; }
    }
}

page 313400 "PSTRA Demo ListPart"
{
    PageType = ListPart;
    SourceTable = "PSTRA Demo Tbl";
    SourceTableTemporary = true;
    Editable = true;

    layout
    {
        area(content)
        {
            repeater(Group)
            {
                field("Line No."; Rec."Line No.") { ApplicationArea = All; }
                field("Attribute ID"; Rec."Attribute ID") { ApplicationArea = All; }
                field(Description; Rec.Description) { ApplicationArea = All; }
            }
        }
    }

    internal procedure SetConditions(var Tmp: Record "PSTRA Demo Tbl" temporary)
    begin
        Rec.DeleteAll();

        Tmp.SetFilter("Attribute ID", '<>%1', 0);

        if Tmp.FindSet() then
            repeat
                Rec := Tmp;
                Rec.Insert();
            until Tmp.Next() = 0;

        Tmp.SetRange("Attribute ID");
    end;

    internal procedure CountRows(): Integer
    begin
        exit(Rec.Count());
    end;
}
