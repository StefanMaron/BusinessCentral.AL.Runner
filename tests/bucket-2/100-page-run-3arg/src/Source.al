/// Source codeunit exercising Page.Run / Page.RunModal 3-argument overloads.
/// All 10 overloads from issue #1374 are exercised here.
/// The position/focus argument is accepted and ignored by the runner — no real UI.
codeunit 309600 "P3A Source"
{
    // -----------------------------------------------------------------------
    // Page.Run 3-arg overloads
    // -----------------------------------------------------------------------

    /// Page.Run(Integer, Table, Integer) — position as integer field number
    procedure RunIntTableInt(var Rec: Record "P3A Row")
    begin
        Page.Run(Page::"P3A Card", Rec, 1);
    end;

    /// Page.Run(Integer, Table, Joker) — position as Joker (field variable)
    procedure RunIntTableJoker(var Rec: Record "P3A Row")
    begin
        Page.Run(Page::"P3A Card", Rec, Rec.Id);
    end;

    /// Page.Run(Text, Table, Integer) — page name as Text, position as integer
    procedure RunTextTableInt(var Rec: Record "P3A Row"; PageName: Text)
    begin
        Page.Run(PageName, Rec, 1);
    end;

    /// Page.Run(Text, Table, Joker) — page name as Text, position as Joker
    procedure RunTextTableJoker(var Rec: Record "P3A Row"; PageName: Text)
    begin
        Page.Run(PageName, Rec, Rec.Id);
    end;

    // -----------------------------------------------------------------------
    // Page.RunModal 3-arg overloads
    // -----------------------------------------------------------------------

    /// Page.RunModal(Integer, Table, FieldRef) — position as FieldRef
    procedure RunModalIntTableFieldRef(var Rec: Record "P3A Row"): Action
    var
        RRef: RecordRef;
        FRef: FieldRef;
    begin
        RRef.GetTable(Rec);
        FRef := RRef.Field(1);
        exit(Page.RunModal(Page::"P3A Card", Rec, FRef));
    end;

    /// Page.RunModal(Integer, Table, Integer) — position as integer field number
    procedure RunModalIntTableInt(var Rec: Record "P3A Row"): Action
    begin
        exit(Page.RunModal(Page::"P3A Card", Rec, 1));
    end;

    /// Page.RunModal(Integer, Table, Joker) — position as Joker (field variable)
    procedure RunModalIntTableJoker(var Rec: Record "P3A Row"): Action
    begin
        exit(Page.RunModal(Page::"P3A Card", Rec, Rec.Id));
    end;

    /// Page.RunModal(Text, Table, FieldRef) — page name as Text, position as FieldRef
    procedure RunModalTextTableFieldRef(var Rec: Record "P3A Row"; PageName: Text): Action
    var
        RRef: RecordRef;
        FRef: FieldRef;
    begin
        RRef.GetTable(Rec);
        FRef := RRef.Field(1);
        exit(Page.RunModal(PageName, Rec, FRef));
    end;

    /// Page.RunModal(Text, Table, Integer) — page name as Text, position as integer
    procedure RunModalTextTableInt(var Rec: Record "P3A Row"; PageName: Text): Action
    begin
        exit(Page.RunModal(PageName, Rec, 1));
    end;

    /// Page.RunModal(Text, Table, Joker) — page name as Text, position as Joker
    procedure RunModalTextTableJoker(var Rec: Record "P3A Row"; PageName: Text): Action
    begin
        exit(Page.RunModal(PageName, Rec, Rec.Id));
    end;
}

table 309600 "P3A Row"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 309600 "P3A Card"
{
    PageType = Card;
    SourceTable = "P3A Row";

    layout
    {
        area(Content)
        {
            field(IdField; Rec.Id) { }
            field(NameField; Rec.Name) { }
        }
    }
}
