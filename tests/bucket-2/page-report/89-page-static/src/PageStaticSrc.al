/// Source codeunit + fixtures exercising Page.* method calls.
/// Static calls (Page.Run, Page.RunModal) and instance calls on a page variable.
codeunit 89100 "PST Source"
{
    // ------------------------------------------------------------------
    // Static Page.* calls
    // ------------------------------------------------------------------

    procedure CallPageRun(PageId: Integer)
    begin
        Page.Run(PageId);
    end;

    procedure CallPageRunModal(PageId: Integer): Action
    begin
        exit(Page.RunModal(PageId));
    end;

    // ------------------------------------------------------------------
    // Instance calls on a page variable
    // ------------------------------------------------------------------

    procedure CallPageActivate()
    var
        P: Page "PST Card";
    begin
        P.Activate();
    end;

    procedure CallPageSaveRecord()
    var
        P: Page "PST Card";
    begin
        P.SaveRecord();
    end;

    procedure CallPageUpdate()
    var
        P: Page "PST Card";
    begin
        P.Update();
    end;

    procedure CallPageUpdateBool(DoUpdate: Boolean)
    var
        P: Page "PST Card";
    begin
        P.Update(DoUpdate);
    end;

    procedure CallPageSetTableView(var Rec: Record "PST Record")
    var
        P: Page "PST Card";
    begin
        P.SetTableView(Rec);
    end;

    procedure CallPageSetSelectionFilter(var Rec: Record "PST Record")
    var
        P: Page "PST Card";
    begin
        P.SetSelectionFilter(Rec);
    end;

    procedure CallPageSetRecord(var Rec: Record "PST Record")
    var
        P: Page "PST Card";
    begin
        P.SetRecord(Rec);
    end;

    procedure GetPageObjectId(): Text
    var
        P: Page "PST Card";
    begin
        exit(P.ObjectId(false));
    end;

    procedure GetPageLookupMode(): Boolean
    var
        P: Page "PST Card";
    begin
        exit(P.LookupMode);
    end;

    procedure SetAndGetPageLookupMode(Value: Boolean): Boolean
    var
        P: Page "PST Card";
    begin
        P.LookupMode := Value;
        exit(P.LookupMode);
    end;
}

table 89100 "PST Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 89100 "PST Card"
{
    PageType = Card;
    SourceTable = "PST Record";

    layout
    {
        area(Content)
        {
            field(IdField; Rec.Id) { }
            field(NameField; Rec.Name) { }
        }
    }
}
