/// <summary>#5309: where the library's members are generated, into the library's own compile.</summary>
codeunit 72820 "TPG Lib Target"
{
    procedure Placeholder()
    begin
    end;
}

table 72821 "TPG Rec"
{
    fields { field(1; PK; Code[20]) { } field(2; Qty; Integer) { } }
    keys { key(PK; PK) { Clustered = true; } }
}

page 72822 "TPG Card"
{
    PageType = Card;
    SourceTable = "TPG Rec";
    layout
    {
        area(Content)
        {
            field(PK; Rec.PK) { }
            field(Qty; Rec.Qty)
            {
                trigger OnValidate() var T: Codeunit "TPG Lib Target"; R: Integer; begin R := T.MLibValidate(1); end;
            }
        }
    }
    actions
    {
        area(Processing)
        {
            action(Go) { trigger OnAction() var T: Codeunit "TPG Lib Target"; R: Integer; begin R := T.MLibAction(1); end; }
            action(Other) { trigger OnAction() var T: Codeunit "TPG Lib Target"; R: Integer; begin R := T.MLibOther(1); end; }
        }
    }
    trigger OnOpenPage() var T: Codeunit "TPG Lib Target"; R: Integer; begin R := T.MLibOpen(1); end;
}

/// <summary>A procedure of the library that opens the page: the test bundle calls it, and the page extension and
/// the subscriber that react to the open are in the test bundle, compiled after this one.</summary>
codeunit 72823 "TPG Helper"
{
    procedure OpenThePage()
    var P: TestPage "TPG Card";
    begin
        P.OpenNew();
        P.Close();
    end;
}
