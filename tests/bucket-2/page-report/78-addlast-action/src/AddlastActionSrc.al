/// Table backing the base page used by the page extension in this suite.
table 50786 "ALA Product"
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

/// Base page with an existing action so the pageextension has something to addlast inside.
page 50786 "ALA Product Card"
{
    PageType = Card;
    SourceTable = "ALA Product";

    actions
    {
        area(Processing)
        {
            action(ExistingAction)
            {
                ApplicationArea = All;
                Caption = 'Existing';
                trigger OnAction()
                begin
                end;
            }
        }
    }
}

/// pageextension using addlast in the actions area (single action appended).
/// This is the exact construct issue #415 says fails to compile.
pageextension 50786 "ALA Product Card Last" extends "ALA Product Card"
{
    actions
    {
        addlast(Processing)
        {
            action(LastAction)
            {
                ApplicationArea = All;
                Caption = 'Last';
                trigger OnAction()
                var
                    Helper: Codeunit "ALA Product Helper";
                begin
                    Message(Helper.GetLabel());
                end;
            }
        }
    }
}

/// pageextension using addlast in the actions area with multiple actions appended.
/// Proves addlast with multiple action bodies compiles.
pageextension 50787 "ALA Product Card LastMulti" extends "ALA Product Card"
{
    actions
    {
        addlast(Processing)
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
/// Proves the compilation unit containing pageextensions with addlast in the
/// actions area compiles and codeunits alongside remain callable.
codeunit 50786 "ALA Product Helper"
{
    procedure GetLabel(): Text
    begin
        exit('LastAction');
    end;

    procedure MultiplyPlusOne(a: Integer; b: Integer): Integer
    begin
        exit(a * b + 1);
    end;

    procedure Wrap(s: Text): Text
    begin
        exit('[' + s + ']');
    end;
}
