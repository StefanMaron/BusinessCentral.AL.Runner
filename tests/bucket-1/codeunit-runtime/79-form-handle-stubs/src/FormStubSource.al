// Renumbered from 57900 to avoid collision in new bucket layout (#1385).
table 1057900 "Form Stub Data"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 57900 "Form Stub Page"
{
    PageType = Card;
    SourceTable = "Form Stub Data";

    layout
    {
        area(Content)
        {
            field(NameField; Rec.Name) { }
        }
    }

    actions
    {
        area(Processing)
        {
            action(MyCustomAction)
            {
                trigger OnAction()
                begin
                end;
            }
        }
    }
}

codeunit 57900 "Form Stub Logic"
{
    /// Exercises Page variable stubs (SetTableView, LookupMode, Editable, PageCaption, Clear, GetRecord)
    procedure ExercisePageStubs()
    var
        Rec: Record "Form Stub Data";
        P: Page "Form Stub Page";
        IsLookup: Boolean;
        IsEdit: Boolean;
        Cap: Text;
    begin
        P.SetTableView(Rec);
        IsLookup := P.LookupMode;
        P.LookupMode := true;
        IsEdit := P.Editable;
        P.Editable := false;
        Cap := P.Caption;
        P.Caption := 'hello';
        Clear(P);
        P.GetRecord(Rec);
    end;

    procedure GetLookupMode(): Boolean
    var
        P: Page "Form Stub Page";
    begin
        exit(P.LookupMode);
    end;

    procedure GetEditable(): Boolean
    var
        P: Page "Form Stub Page";
    begin
        exit(P.Editable);
    end;

    procedure GetCaption(): Text
    var
        P: Page "Form Stub Page";
    begin
        exit(P.Caption);
    end;
}
