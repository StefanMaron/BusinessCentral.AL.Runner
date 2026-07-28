table 62090 "TPE Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[50]) { }
        field(3; Note; Text[50]) { }
        // Stands in for any "this row is owned by someone else" discriminator — a scope
        // enum, an ownership flag, a posted/open state. The page turns it into editability.
        field(4; Locked; Boolean) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// <summary>
/// A card whose read-only contract is expressed the two ways AL expresses it: a constant
/// <c>Editable = false</c> for a control that is never writable, and
/// <c>Editable = RowEditable</c> for one that depends on the row currently loaded.
///
/// Both are ordinary AL and both are how a real app protects data it does not own. The page
/// also flips <c>CurrPage.Editable</c> so the page-level state is exercised alongside the
/// per-control state — they are different mechanisms and a runner can get one right and the
/// other wrong.
/// </summary>
page 62090 "TPE Card"
{
    PageType = Card;
    SourceTable = "TPE Row";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            group(General)
            {
                // Never editable, regardless of the row: the primary key of an existing row.
                field("No."; Rec."No.")
                {
                    ApplicationArea = All;
                    Editable = false;
                }
                // Editable only for rows this page owns.
                field(Name; Rec.Name)
                {
                    ApplicationArea = All;
                    Editable = RowEditable;
                }
                // No Editable property at all — the default. This is the control that stops a
                // "return false everywhere" fix from passing.
                field(Note; Rec.Note)
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
            action(Rename)
            {
                ApplicationArea = All;
                Caption = 'Rename';
                Enabled = RowEditable;

                trigger OnAction()
                begin
                    Rec.Name := 'renamed';
                    Rec.Modify();
                end;
            }
            action(Refresh)
            {
                ApplicationArea = All;
                Caption = 'Refresh';

                trigger OnAction()
                begin
                    CurrPage.Update(false);
                end;
            }
        }
    }

    var
        RowEditable: Boolean;

    trigger OnAfterGetRecord()
    begin
        RowEditable := not Rec.Locked;
        CurrPage.Editable(RowEditable);
    end;
}
