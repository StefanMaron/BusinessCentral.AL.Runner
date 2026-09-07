// An ordinary editable, insert-allowed repeater, so it shows the implicit blank draft line past
// its data the way every Base Application line grid does. AutoSplitKey for the same reason those
// grids have it — a row promoted out of the draft line must be numbered past the rows already
// present.
page 70644 "ONC Lines"
{
    PageType = ListPart;
    SourceTable = "ONC Line";
    ApplicationArea = All;
    AutoSplitKey = true;

    layout
    {
        area(Content)
        {
            repeater(Lines)
            {
                field(HeaderNo; Rec."Header No.") { ApplicationArea = All; }
                field(LineNo; Rec."Line No.") { ApplicationArea = All; }
                field(Descr; Rec.Descr) { ApplicationArea = All; }
            }
        }
    }

    // ONE LOG ROW PER FIRING — the whole point of the fixture.
    trigger OnNewRecord(BelowxRec: Boolean)
    var
        Log: Record "ONC Log";
    begin
        Log.Init();
        Log.Source := 'LINES';
        Log.Insert(true);
    end;
}
