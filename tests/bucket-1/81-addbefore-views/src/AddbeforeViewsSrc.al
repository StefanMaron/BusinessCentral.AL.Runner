/// Table backing the base page used by the page extension in this suite.
table 50816 "ABV Product"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Active; Boolean) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// Base page with an existing view so the pageextension has something to addbefore.
page 50816 "ABV Product List"
{
    PageType = List;
    SourceTable = "ABV Product";

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(Code; Rec.Code) { }
                field(Active; Rec.Active) { }
            }
        }
    }

    views
    {
        view(ExistingView)
        {
            Caption = 'Existing View';
        }
    }
}

/// pageextension using addbefore inside the views area.
/// This is the exact construct issue #424 says fails to compile.
pageextension 50816 "ABV Product List Before" extends "ABV Product List"
{
    views
    {
        addbefore(ExistingView)
        {
            view(NewBeforeView)
            {
                Caption = 'Before View';
                Filters = where(Active = const(true));
            }
        }
    }
}

/// pageextension using addbefore in views with multiple views inserted.
/// Proves addbefore with multiple view declarations compiles.
pageextension 50817 "ABV Product List BeforeMulti" extends "ABV Product List"
{
    views
    {
        addbefore(ExistingView)
        {
            view(ViewAlpha)
            {
                Caption = 'Alpha';
            }
            view(ViewBeta)
            {
                Caption = 'Beta';
                Filters = where(Active = const(false));
            }
        }
    }
}

/// Helper codeunit with business logic exercised by the tests.
/// Proves the compilation unit containing pageextensions with addbefore in the
/// views area compiles and codeunits alongside remain callable.
codeunit 50816 "ABV Product Helper"
{
    procedure GetViewLabel(): Text
    begin
        exit('BeforeView');
    end;

    procedure DoublePlusThree(n: Integer): Integer
    begin
        exit(n * 2 + 3);
    end;

    procedure Tag(s: Text): Text
    begin
        exit('<' + s + '>');
    end;
}
