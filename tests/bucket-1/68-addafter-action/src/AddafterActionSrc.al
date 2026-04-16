/// Table backing the base page used by the page extension in this suite.
table 50406 "AAA Product"
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

/// Base page with a "New" action so the pageextension has something to addafter.
page 50406 "AAA Product Card"
{
    PageType = Card;
    SourceTable = "AAA Product";

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

/// pageextension using addafter in the actions area (single action inserted).
/// This is the exact construct issue #406 says fails to compile.
pageextension 50406 "AAA Product Card After" extends "AAA Product Card"
{
    actions
    {
        addafter("New")
        {
            action(MyAction)
            {
                ApplicationArea = All;
                Caption = 'My Action';
                trigger OnAction()
                var
                    Helper: Codeunit "AAA Product Helper";
                begin
                    Message(Helper.GetMessage());
                end;
            }
        }
    }
}

/// pageextension using addafter in the actions area with multiple actions inserted.
/// Proves addafter with an action group (multiple action bodies) compiles.
pageextension 50407 "AAA Product Card AfterMulti" extends "AAA Product Card"
{
    actions
    {
        addafter("New")
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
/// Proves that the compilation unit containing pageextensions with addafter
/// in the actions area compiles and codeunits alongside remain callable.
codeunit 50406 "AAA Product Helper"
{
    procedure GetMessage(): Text
    begin
        exit('hello');
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
