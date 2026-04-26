/// Table backing the base page used by the page extension in this suite.
table 50796 "AFA Product"
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

/// Base page with an existing action so the pageextension has something to addfirst against.
page 50796 "AFA Product Card"
{
    PageType = Card;
    SourceTable = "AFA Product";

    actions
    {
        area(Processing)
        {
            action("Existing")
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                end;
            }
        }
    }
}

/// pageextension using addfirst in the actions area (single action inserted).
/// This is the exact construct issue #413 says fails to compile.
pageextension 50796 "AFA Product Card First" extends "AFA Product Card"
{
    actions
    {
        addfirst(Processing)
        {
            action(MyFirstAction)
            {
                ApplicationArea = All;
                Caption = 'My First Action';
                trigger OnAction()
                var
                    Helper: Codeunit "AFA Product Helper";
                begin
                    Message(Helper.GetMessage());
                end;
            }
        }
    }
}

/// pageextension using addfirst in the actions area with multiple actions inserted.
/// Proves addfirst with multiple action bodies compiles.
pageextension 50797 "AFA Product Card FirstMulti" extends "AFA Product Card"
{
    actions
    {
        addfirst(Processing)
        {
            action(ActionAlpha)
            {
                ApplicationArea = All;
                Caption = 'Alpha';
                trigger OnAction()
                begin
                end;
            }
            action(ActionBeta)
            {
                ApplicationArea = All;
                Caption = 'Beta';
                trigger OnAction()
                begin
                end;
            }
        }
    }
}

/// Helper codeunit with business logic exercised by the tests.
/// Proves that the compilation unit containing pageextensions with addfirst
/// in the actions area compiles and codeunits alongside remain callable.
codeunit 50796 "AFA Product Helper"
{
    procedure GetMessage(): Text
    begin
        exit('first');
    end;

    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;

    procedure Concat(a: Text; b: Text): Text
    begin
        exit(a + '|' + b);
    end;
}
