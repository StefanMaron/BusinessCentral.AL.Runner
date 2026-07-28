table 62140 "TOV Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }

        // The realistic shape: setting a code fills in the name that belongs to it. Pageworks'
        // ReportId → ReportCaption is exactly this, and it is what fails when a page assigns
        // instead of validating.
        field(2; Source; Code[20])
        {
            trigger OnValidate()
            begin
                if Rec.Source = '' then
                    Rec.Derived := ''
                else
                    Rec.Derived := 'derived-from-' + Rec.Source;
            end;
        }
        field(3; Derived; Text[30]) { Editable = false; }

        // No trigger at all — the control for "the runner did not simply start running
        // something on every write".
        field(4; Manual; Text[30]) { }

        // Refuses a value outright, so the write must not land.
        field(5; Guarded; Integer)
        {
            trigger OnValidate()
            begin
                if Rec.Guarded < 0 then
                    Error('Guarded may not be negative, but %1 was entered.', Rec.Guarded);
            end;
        }

        // Written only by the PAGE control's OnValidate, never by the table's.
        field(6; PageEcho; Text[30]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

page 62140 "TOV Card"
{
    PageType = Card;
    SourceTable = "TOV Row";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            group(General)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(Source; Rec.Source) { ApplicationArea = All; }
                field(Derived; Rec.Derived) { ApplicationArea = All; }
                field(Manual; Rec.Manual) { ApplicationArea = All; }
                field(Guarded; Rec.Guarded) { ApplicationArea = All; }
                field(PageEcho; Rec.PageEcho) { ApplicationArea = All; }

                // A control's own OnValidate is a second, independent trigger from the table
                // field's, and a runner can wire one without the other. Bound to the SAME field
                // as the plain Manual control above, so the only difference between the two is
                // this trigger — which is exactly what the test needs to attribute the effect to.
                field(Watched; Rec.Manual)
                {
                    ApplicationArea = All;
                    Caption = 'Watched';

                    trigger OnValidate()
                    begin
                        Rec.PageEcho := 'control-saw-' + Rec.Manual;
                    end;
                }
            }
        }
    }
}
