table 62110 "TRT Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        // Deliberately ordered so the DEFAULT (0) is the wrong answer. A page that seeds this
        // in OnNewRecord and a runner that skips the trigger differ visibly; if Tenant were 0
        // the bug would be invisible.
        field(2; Kind; Option) { OptionMembers = Extension,Tenant; }
        field(3; Note; Text[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// <summary>Observation sink — page triggers write here so a test can see they ran.</summary>
table 62111 "TRT Echo"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Key"; Code[20]) { }
        field(2; Hits; Integer) { }
    }

    keys
    {
        key(PK; "Key") { Clustered = true; }
    }

    procedure Bump(Name: Code[20])
    var
        Echo: Record "TRT Echo";
    begin
        if Echo.Get(Name) then begin
            Echo.Hits += 1;
            Echo.Modify();
        end else begin
            Echo.Init();
            Echo."Key" := Name;
            Echo.Hits := 1;
            Echo.Insert();
        end;
    end;
}

page 62110 "TRT Card"
{
    PageType = Card;
    SourceTable = "TRT Row";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            group(General)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(Kind; Rec.Kind) { ApplicationArea = All; }
                field(Note; Rec.Note) { ApplicationArea = All; }
            }
        }
    }

    trigger OnNewRecord(BelowxRec: Boolean)
    begin
        // A brand-new row belongs to the tenant; the enum's own default (Extension) is what a
        // blank record would carry, and is wrong.
        Rec.Validate(Kind, Rec.Kind::Tenant);
    end;

    trigger OnInsertRecord(BelowxRec: Boolean): Boolean
    begin
        // The page's last word before the row is persisted.
        Rec.Note := 'stamped-by-oninsert';
        exit(true);
    end;

    trigger OnAfterGetCurrRecord()
    var
        Echo: Record "TRT Echo";
    begin
        Echo.Bump('CURR');
    end;
}

/// <summary>A card whose OnInsertRecord vetoes the insert by returning false.</summary>
page 62111 "TRT Card No Insert"
{
    PageType = Card;
    SourceTable = "TRT Row";
    ApplicationArea = All;
    UsageCategory = Administration;

    layout
    {
        area(Content)
        {
            group(General)
            {
                field("No."; Rec."No.") { ApplicationArea = All; }
                field(Note; Rec.Note) { ApplicationArea = All; }
            }
        }
    }

    trigger OnInsertRecord(BelowxRec: Boolean): Boolean
    begin
        exit(false);
    end;
}
