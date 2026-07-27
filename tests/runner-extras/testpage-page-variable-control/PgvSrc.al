table 61990 "PGV Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Descr; Text[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// <summary>
/// A list page whose first control binds to a page GLOBAL VARIABLE rather than to a
/// source-table field — the standard AL shape for a mode/filter selector above a
/// repeater, and exactly what four Pageworks pages do (KindSelector/SelectedKind,
/// ContentField/ContentText, Channel/ChannelTxt).
///
/// The OnValidate trigger writes into the table so the test can observe, from outside
/// the page, that setting the control actually ran the page's AL — not merely that a
/// value was stashed somewhere and handed back.
/// </summary>
page 61990 "PGV List"
{
    PageType = List;
    SourceTable = "PGV Row";
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
            field(Mode; SelectedMode)
            {
                ApplicationArea = All;
                Caption = 'Mode';

                trigger OnValidate()
                var
                    Echo: Record "PGV Row";
                begin
                    if Echo.Get('ECHO') then begin
                        Echo.Descr := SelectedMode;
                        Echo.Modify();
                    end else begin
                        Echo.Init();
                        Echo."No." := 'ECHO';
                        Echo.Descr := SelectedMode;
                        Echo.Insert();
                    end;
                end;
            }
            repeater(Rows)
            {
                field("No."; Rec."No.")
                {
                    ApplicationArea = All;
                }
                field(Descr; Rec.Descr)
                {
                    ApplicationArea = All;
                }
            }
        }
    }

    var
        SelectedMode: Text[30];
}
