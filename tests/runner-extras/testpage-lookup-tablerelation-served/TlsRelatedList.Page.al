// The page "Tls Related".LookupPageId names. A lookup on a "Tls Host" field related to that
// table must open THIS page, and the bundle's [ModalPageHandler] is declared for it -- which is
// how the test can tell "the relation was followed" from "something was opened".
page 65791 "Tls Related List"
{
    PageType = List;
    SourceTable = "Tls Related";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            repeater(Rows)
            {
                field("Code"; Rec."Code") { ApplicationArea = All; }
                field(Descr; Rec.Descr) { ApplicationArea = All; }
                field(Blocked; Rec.Blocked) { ApplicationArea = All; }
            }
        }
    }
}
