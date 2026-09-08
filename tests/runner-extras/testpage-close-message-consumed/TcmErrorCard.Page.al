/// <summary>
/// A card whose OnQueryClosePage always raises an AL error, and which writes a row from
/// OnOpenPage. Shaped after the corpus fixture "QCE Error Card" (page 60678) so the runner
/// meets the same shape a real service tier already adjudicated, but with its own object ids
/// -- this bundle stands alone and does not depend on the corpus.
/// </summary>
page 65862 "Tcm Error Card"
{
    PageType = Card;
    SourceTable = "Tcm Row";
    ApplicationArea = All;
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
            field("Set ID"; Rec."Set ID") { ApplicationArea = All; }
        }
    }

    var
        CloseRefusedErr: Label 'TCM close refused by OnQueryClosePage';

    trigger OnOpenPage()
    var
        Row: Record "Tcm Row";
    begin
        if not Row.Get('OPENED') then begin
            Row.Init();
            Row."No." := 'OPENED';
            Row."Set ID" := 42;
            Row.Insert();
        end;
    end;

    trigger OnQueryClosePage(CloseAction: Action): Boolean
    begin
        Error(CloseRefusedErr);
    end;
}
