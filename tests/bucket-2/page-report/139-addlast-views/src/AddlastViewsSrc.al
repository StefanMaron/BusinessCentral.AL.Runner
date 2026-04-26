/// Table backing the base page used by the page extension in this suite.
table 60400 "ALV Product"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Active; Boolean) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// Base page with an existing view so the pageextension has something to append after.
page 60400 "ALV Product List"
{
    PageType = List;
    SourceTable = "ALV Product";

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(CodeField; Rec.Code) { ApplicationArea = All; }
                field(NameField; Rec.Name) { ApplicationArea = All; }
                field(ActiveField; Rec.Active) { ApplicationArea = All; }
            }
        }
    }

    views
    {
        view(AllRecords)
        {
            Caption = 'All Records';
        }
    }
}

/// Page extension using addlast() in the views area — appends a view at the end
/// of the views section. addlast in views is a compile-time directive; it has
/// no runtime effect in unit-test context, so proving compilation is sufficient.
pageextension 60400 "ALV Product List Ext" extends "ALV Product List"
{
    views
    {
        addlast
        {
            view(ActiveOnly)
            {
                Caption = 'Active Only';
                Filters = where(Active = const(true));
            }
            view(InactiveOnly)
            {
                Caption = 'Inactive Only';
                Filters = where(Active = const(false));
            }
        }
    }
}

/// Business logic helper — proves that the compilation unit containing a
/// pageextension with addlast(views) compiles and executes logic correctly.
codeunit 60400 "ALV Helper"
{
    procedure GetLabel(): Text
    begin
        exit('addlast views ok');
    end;

    procedure IsActive(Active: Boolean): Text
    begin
        if Active then
            exit('Active');
        exit('Inactive');
    end;

    procedure FormatCode(Code: Code[20]; Name: Text): Text
    begin
        exit(Code + ': ' + Name);
    end;
}
