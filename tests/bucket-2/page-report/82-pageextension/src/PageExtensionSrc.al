/// Table backing the base page used by the page extension in this suite.
table 50826 "PXT Customer"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Balance; Decimal) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Base page with an existing field and action so the pageextension has
/// anchors to addafter.
page 50826 "PXT Customer List"
{
    PageType = List;
    SourceTable = "PXT Customer";

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field("No."; Rec."No.") { }
                field(Name; Rec.Name) { }
            }
        }
    }

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

/// Comprehensive pageextension — this is the construct issue #370 says the
/// runner should compile: a single extension that combines layout addafter,
/// actions addafter, local variables, and a page trigger.
pageextension 50826 "PXT Customer List Ext" extends "PXT Customer List"
{
    layout
    {
        addafter(Name)
        {
            field(Balance; Rec.Balance) { }
            field(DerivedLabel; DerivedLabel)
            {
                Editable = false;
            }
        }
    }

    actions
    {
        addafter(ExistingAction)
        {
            action(NewAction)
            {
                ApplicationArea = All;
                Caption = 'New';
                trigger OnAction()
                var
                    Helper: Codeunit "PXT Customer Helper";
                begin
                    Message(Helper.BuildCaption('run'));
                end;
            }
        }
    }

    var
        DerivedLabel: Text;

    trigger OnOpenPage()
    var
        Helper: Codeunit "PXT Customer Helper";
    begin
        DerivedLabel := Helper.BuildCaption('opened');
    end;

    trigger OnAfterGetCurrRecord()
    var
        Helper: Codeunit "PXT Customer Helper";
    begin
        DerivedLabel := Helper.BuildCaption(Rec."No.");
    end;
}

/// Helper codeunit with business logic exercised by the tests and by the
/// pageextension's triggers/actions. Proves that the compilation unit
/// containing a multi-feature pageextension compiles and stays live.
codeunit 50826 "PXT Customer Helper"
{
    procedure BuildCaption(context: Text): Text
    begin
        exit('[PXT] ' + context);
    end;

    procedure NextBalance(current: Decimal; delta: Decimal): Decimal
    begin
        exit(current + delta + 1);
    end;

    procedure IsPositive(value: Decimal): Boolean
    begin
        exit(value > 0);
    end;
}
