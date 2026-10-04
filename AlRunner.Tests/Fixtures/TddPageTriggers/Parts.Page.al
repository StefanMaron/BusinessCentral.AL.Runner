// A part runs its own page's triggers when the page that hosts it opens, and when its rows move.
page 72808 "PT Lines Part"
{
    PageType = ListPart;
    SourceTable = "PT Quiet";
    layout
    {
        area(Content)
        {
            repeater(Rows) { field(K; Rec.K) { } }
        }
    }
    actions { area(Processing) { action(PartGo) { trigger OnAction() var T: Codeunit "PT Target"; R: Integer; begin R := T.MPartGo(1); end; } } }
    trigger OnOpenPage() var T: Codeunit "PT Target"; R: Integer; begin R := T.MPartOpen(1); end;
    trigger OnModifyRecord(): Boolean var T: Codeunit "PT Target"; R: Integer; begin R := T.MPartModify(1); exit(true); end;
}

page 72809 "PT Parent"
{
    PageType = Card;
    SourceTable = "PT Quiet";
    layout
    {
        area(Content)
        {
            field(K; Rec.K) { }
            part(Lines; "PT Lines Part") { }
            // A part inside a group: opening the page opens it too.
            group(Details) { part(Leaf; "PT Leaf Part") { } }
        }
    }
}

page 72817 "PT Leaf Part"
{
    PageType = ListPart;
    SourceTable = "PT Quiet";
    layout { area(Content) { repeater(Rows) { field(K; Rec.K) { } } } }
    trigger OnOpenPage() var T: Codeunit "PT Target"; R: Integer; begin R := T.MLeafOpen(1); end;
}
