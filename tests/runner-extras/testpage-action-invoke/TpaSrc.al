table 61940 "TPA Row"
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
/// A list page with page actions. StampRow writes what the page's CURRENT row is, so a
/// test can tell "the trigger ran" apart from "the trigger ran in the page's context".
/// </summary>
page 61940 "TPA List"
{
    PageType = List;
    SourceTable = "TPA Row";
    ApplicationArea = All;
    UsageCategory = Lists;

    layout
    {
        area(Content)
        {
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

    actions
    {
        area(Processing)
        {
            action(StampRow)
            {
                ApplicationArea = All;
                Caption = 'Stamp Row';

                trigger OnAction()
                var
                    Stamp: Record "TPA Row";
                begin
                    if not Stamp.Get('STAMP') then begin
                        Stamp.Init();
                        Stamp."No." := 'STAMP';
                        Stamp.Descr := Rec."No.";
                        Stamp.Insert();
                    end else begin
                        Stamp.Descr := Rec."No.";
                        Stamp.Modify();
                    end;
                end;
            }

            action(StampOther)
            {
                ApplicationArea = All;
                Caption = 'Stamp Other';

                trigger OnAction()
                var
                    Stamp: Record "TPA Row";
                begin
                    Stamp.Init();
                    Stamp."No." := 'OTHER';
                    Stamp.Descr := 'other ran';
                    Stamp.Insert();
                end;
            }

            action(AlwaysFails)
            {
                ApplicationArea = All;
                Caption = 'Always Fails';

                trigger OnAction()
                begin
                    Error('TPA action refused deliberately');
                end;
            }
        }
    }
}
