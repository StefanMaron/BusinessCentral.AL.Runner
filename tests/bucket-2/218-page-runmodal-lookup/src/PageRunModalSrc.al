page 60470 "PRM Card"
{
    PageType = Card;
    SourceTable = "PRM Source";

    layout
    {
        area(Content)
        {
            field(Id; Rec."Id") { ApplicationArea = All; }
        }
    }
}

table 60470 "PRM Source"
{
    fields
    {
        field(1; "Id"; Integer) { }
    }
    keys { key(PK; "Id") { Clustered = true; } }
}

/// Exercises Page<N>.RunModal and LookupMode on a generated page class.
codeunit 60470 "PRM Src"
{
    procedure PageRunModal_ReturnsAction(): Action
    var
        pg: Page "PRM Card";
    begin
        exit(pg.RunModal());
    end;

    procedure PageLookupMode_SetAndGet(): Boolean
    var
        pg: Page "PRM Card";
    begin
        pg.LookupMode := true;
        exit(pg.LookupMode);
    end;
}
