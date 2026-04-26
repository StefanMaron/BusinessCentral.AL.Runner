/// Exercises scenarios where BC-generated C# requires Page<N> to be implicitly
/// convertible to NavForm.  After stripping NavForm from the Page<N> base-class
/// list, any BC-internal method that passes a page instance where NavForm is
/// expected raises CS1503 at Roslyn compile time (issue #1106).
///
/// Fix: inject `public static implicit operator NavForm(PageN p) => default!`
/// into every generated Page<N> class so implicit conversions succeed.

codeunit 29100 "PRV Source"
{
    /// Calls Page.Run with a record variable — BC lowers to NavForm.Run(pageId, rec.Target).
    /// The record arg (MockRecordHandle) is not NavForm, so this tests the most common
    /// Page.Run overload still compiles after NavForm stripping.
    procedure RunWithRecord(var Rec: Record "PRV Row")
    begin
        Page.Run(Page::"PRV Card", Rec);
    end;

    /// Calls Page.RunModal with a record — BC lowers to NavForm.RunModal(false,true,pageId,rec.Target).
    /// Tests that RunModal with record compiles and returns Action.
    procedure RunModalWithRecord(var Rec: Record "PRV Row"): Action
    begin
        exit(Page.RunModal(Page::"PRV Card", Rec));
    end;

    /// Uses a Page variable directly — BC emits NavFormHandle (→ MockFormHandle).
    /// Exercises that page variable operations compile even when NavForm base is stripped.
    procedure PageVarRun()
    var
        P: Page "PRV Card";
    begin
        P.Run();
    end;

    /// Uses a Page variable with SetRecord — BC emits p.Target.SetRecord(rec.Target).
    /// Exercises the handle-based path that already works.
    procedure PageVarSetRecord(var Rec: Record "PRV Row")
    var
        P: Page "PRV Card";
    begin
        P.SetRecord(Rec);
        P.Run();
    end;

    /// Calls Page.RunModal with the page variable's record arg pattern.
    procedure PageVarRunModal(): Action
    var
        P: Page "PRV Card";
    begin
        exit(P.RunModal());
    end;
}

table 29100 "PRV Row"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 29100 "PRV Card"
{
    PageType = Card;
    SourceTable = "PRV Row";

    layout
    {
        area(Content)
        {
            field(IdField; Rec.Id) { }
            field(NameField; Rec.Name) { }
        }
    }
}
