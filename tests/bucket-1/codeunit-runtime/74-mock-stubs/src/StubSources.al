table 57400 "Stub Test Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 57400 "Stub Test Card"
{
    PageType = Card;
    SourceTable = "Stub Test Table";

    layout
    {
        area(Content)
        {
            field(NameField; Rec.Name) { }
            field(AmountField; Rec.Amount) { }
        }
    }
}

codeunit 57400 "Stub Logic"
{
    procedure UseRecRefLoadFieldsAndName()
    var
        RecRef: RecordRef;
        TableName: Text;
    begin
        RecRef.Open(57400);
        RecRef.SetLoadFields(1, 2);
        TableName := RecRef.Name;
        RecRef.Close();
    end;

    procedure UsePageUpdate()
    var
        P: Page "Stub Test Card";
    begin
        P.Update();
        P.Update(false);
    end;

    procedure GetTestPageFieldCaption(): Text
    var
        TP: TestPage "Stub Test Card";
        Cap: Text;
    begin
        TP.OpenNew();
        Cap := TP.NameField.Caption;
        TP.Close();
        exit(Cap);
    end;
}
