/// Table backing the base page used by the page extension in this suite.
table 50756 "ABA Product"
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

/// Base page with a "New" action so the pageextension has something to addbefore.
page 50756 "ABA Product Card"
{
    PageType = Card;
    SourceTable = "ABA Product";

    actions
    {
        area(Processing)
        {
            action("New")
            {
                ApplicationArea = All;
                trigger OnAction()
                begin
                end;
            }
        }
    }
}

/// pageextension using addbefore in the actions area (single action inserted).
/// This is the exact construct issue #410 says fails to compile.
pageextension 50756 "ABA Product Card Before" extends "ABA Product Card"
{
    actions
    {
        addbefore("New")
        {
            action(MyBeforeAction)
            {
                ApplicationArea = All;
                Caption = 'My Before Action';
                trigger OnAction()
                var
                    Helper: Codeunit "ABA Product Helper";
                begin
                    Message(Helper.GetMessage());
                end;
            }
        }
    }
}

/// pageextension using addbefore in the actions area with multiple actions inserted.
/// Proves addbefore with an action group (multiple action bodies) compiles.
pageextension 50757 "ABA Product Card BeforeMulti" extends "ABA Product Card"
{
    actions
    {
        addbefore("New")
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
/// Proves that the compilation unit containing pageextensions with addbefore
/// in the actions area compiles and codeunits alongside remain callable.
codeunit 50756 "ABA Product Helper"
{
    procedure GetMessage(): Text
    begin
        exit('before');
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
