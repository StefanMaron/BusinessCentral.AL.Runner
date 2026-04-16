/// Helper codeunit exercising ProductName — issue #755.
codeunit 98000 "TAPN Src"
{
    // ProductName.Full() — must not throw
    procedure ProductNameFull(): Text
    begin
        exit(ProductName.Full());
    end;

    // ProductName.Marketing() — must not throw
    procedure ProductNameMarketing(): Text
    begin
        exit(ProductName.Marketing());
    end;

    // ProductName.Short() — must not throw
    procedure ProductNameShort(): Text
    begin
        exit(ProductName.Short());
    end;
}

// Minimal table and page for TestAction testing
table 98000 "TAPN Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 98000 "TAPN Card"
{
    PageType = Card;
    SourceTable = "TAPN Record";

    layout
    {
        area(Content)
        {
            field(NameField; Rec.Name) { }
        }
    }

    actions
    {
        area(Processing)
        {
            action(DoSomething)
            {
                Caption = 'Do Something';
                trigger OnAction()
                begin
                end;
            }
        }
    }
}
