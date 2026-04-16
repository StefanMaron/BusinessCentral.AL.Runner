/// Table backing the base page used by the page extension in this suite.
table 59510 "MAF Product"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Category; Text[50]) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Base page with three fields and two actions so the pageextension has
/// anchors to moveafter in both layout and actions.
page 59510 "MAF Product List"
{
    PageType = List;
    SourceTable = "MAF Product";

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field("No."; Rec."No.") { }
                field(Name; Rec.Name) { }
                field(Category; Rec.Category) { }
            }
        }
    }

    actions
    {
        area(Processing)
        {
            action(FirstAction)
            {
                ApplicationArea = All;
                Caption = 'First';
                trigger OnAction() begin end;
            }
            action(SecondAction)
            {
                ApplicationArea = All;
                Caption = 'Second';
                trigger OnAction() begin end;
            }
            action(ThirdAction)
            {
                ApplicationArea = All;
                Caption = 'Third';
                trigger OnAction() begin end;
            }
        }
    }
}

/// pageextension using moveafter in the layout area to reposition a field.
/// This is the exact construct issue #432 says fails to compile.
pageextension 59510 "MAF Product List Ext" extends "MAF Product List"
{
    layout
    {
        moveafter(Name; "No.")
    }
}

/// pageextension using moveafter in the actions area to reposition an action,
/// plus a second moveafter in the layout area (multiple moveafter blocks).
pageextension 59511 "MAF Product List Ext2" extends "MAF Product List"
{
    layout
    {
        moveafter(Category; Name)
    }
    actions
    {
        moveafter(FirstAction; SecondAction)
    }
}

/// Helper codeunit with business logic exercised by the tests.
/// Proves the compilation unit containing pageextensions with moveafter
/// compiles and codeunits alongside remain callable.
codeunit 59510 "MAF Product Helper"
{
    procedure GetLabel(): Text
    begin
        exit('moved');
    end;

    procedure TriplePlusFour(n: Integer): Integer
    begin
        exit(n * 3 + 4);
    end;

    procedure Reverse(s: Text): Text
    var
        i: Integer;
        r: Text;
    begin
        r := '';
        for i := StrLen(s) downto 1 do
            r := r + CopyStr(s, i, 1);
        exit(r);
    end;
}
