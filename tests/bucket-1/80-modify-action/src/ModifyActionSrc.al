/// Table backing the base page used by the page extension in this suite.
table 50806 "MAM Product"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Description; Text[100]) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// Base page with an action whose properties the pageextension will modify.
page 50806 "MAM Product Card"
{
    PageType = Card;
    SourceTable = "MAM Product";

    actions
    {
        area(Processing)
        {
            action(MyAction)
            {
                ApplicationArea = All;
                Caption = 'Original Caption';
                trigger OnAction()
                begin
                end;
            }
            action(AnotherAction)
            {
                ApplicationArea = All;
                Caption = 'Another Original';
                Enabled = true;
                trigger OnAction()
                begin
                end;
            }
        }
    }
}

/// pageextension using modify inside the actions area.
/// This is the exact construct issue #422 says fails to compile.
pageextension 50806 "MAM Product Card Mod" extends "MAM Product Card"
{
    actions
    {
        modify(MyAction)
        {
            Caption = 'Modified Caption';
        }
    }
}

/// pageextension using multiple modify blocks in the actions area.
/// Proves multiple modify modifications in one extension compile.
pageextension 50807 "MAM Product Card MultiMod" extends "MAM Product Card"
{
    actions
    {
        modify(MyAction)
        {
            Visible = true;
        }
        modify(AnotherAction)
        {
            Caption = 'Another Modified';
            Enabled = false;
        }
    }
}

/// Helper codeunit with business logic exercised by the tests.
/// Proves the compilation unit containing pageextensions with modify in the
/// actions area compiles and codeunits alongside remain callable.
codeunit 50806 "MAM Product Helper"
{
    procedure GetActionCaption(): Text
    begin
        exit('Modified Caption');
    end;

    procedure SquarePlusTwo(n: Integer): Integer
    begin
        exit(n * n + 2);
    end;

    procedure Quote(s: Text): Text
    begin
        exit('"' + s + '"');
    end;
}
