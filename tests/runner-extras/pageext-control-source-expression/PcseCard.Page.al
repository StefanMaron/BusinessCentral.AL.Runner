/// The BASE page. It declares one control bound to a page GLOBAL (BaseVar) and one bound to a
/// SourceTable field, so the third arm of the test can show the base page itself is readable
/// whatever the extension does -- a failure of the extension arms then cannot be mistaken for
/// the TestPage machinery breaking generally. Mirrors corpus 60978's third arm.
page 65982 "Pcse Card"
{
    PageType = Card;
    SourceTable = "Pcse Row";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            field(BaseField; BaseVar)
            {
                ApplicationArea = All;
                Caption = 'Base Field';
            }
            field("Rec Text"; Rec."Rec Text")
            {
                ApplicationArea = All;
            }
        }
    }

    trigger OnOpenPage()
    begin
        BaseVar := 'base';
    end;

    var
        BaseVar: Text[30];
}
