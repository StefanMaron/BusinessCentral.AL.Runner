/// Table backing the base page used by the page extension in this suite.
table 50856 "AFV Product"
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

/// Base page with an existing view so the pageextension has something to
/// insert before with addfirst in the views area.
page 50856 "AFV Product List"
{
    PageType = List;
    SourceTable = "AFV Product";

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

/// pageextension using addfirst (no anchor argument) inside the views area.
/// This is the exact construct issue #429 says fails to compile.
pageextension 50856 "AFV Product List First" extends "AFV Product List"
{
    views
    {
        addfirst
        {
            view(NewFirstView)
            {
                Caption = 'First View';
                Filters = where(Active = const(true));
            }
        }
    }
}

/// pageextension using addfirst in views with multiple views inserted.
/// Proves addfirst with multiple view declarations compiles.
pageextension 50857 "AFV Product List FirstMulti" extends "AFV Product List"
{
    views
    {
        addfirst
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
/// Proves the compilation unit containing pageextensions with addfirst in the
/// views area compiles and codeunits alongside remain callable.
codeunit 50856 "AFV Product Helper"
{
    procedure GetViewLabel(): Text
    begin
        exit('FirstView');
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
