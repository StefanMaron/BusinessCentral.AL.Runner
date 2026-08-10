/// <summary>
/// The probe table. Declares BOTH page-resolution properties, and a Caption that
/// differs from the object name -- so a provider that filled Caption from Name,
/// or that echoed one page id into both slots, fails on this table alone.
/// </summary>
table 62100 "TMVT Row"
{
    Caption = 'TMVT Probe Row';
    DataClassification = CustomerContent;
    LookupPageId = "TMVT Row List";
    DrillDownPageId = "TMVT Row Card";

    fields
    {
        field(1; "No."; Code[20])
        {
            DataClassification = CustomerContent;
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
    }
}

/// <summary>
/// A table declaring NEITHER LookupPageId NOR DrillDownPageId. Its row must carry
/// 0 in both slots: 0 is the meaningful "this table declares no page" answer that
/// Page Management branches on, so a provider that invented a page id here would
/// be a silent wrong answer, not a harmless one.
/// </summary>
table 62103 "TMVT Plain"
{
    Caption = 'TMVT Plain Row';
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20])
        {
            DataClassification = CustomerContent;
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
    }
}

page 62101 "TMVT Row List"
{
    PageType = List;
    SourceTable = "TMVT Row";
    UsageCategory = None;

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
            }
        }
    }
}

page 62102 "TMVT Row Card"
{
    PageType = Card;
    SourceTable = "TMVT Row";
    UsageCategory = None;

    layout
    {
        area(Content)
        {
            group(General)
            {
                field("No."; Rec."No.")
                {
                    ApplicationArea = All;
                }
            }
        }
    }
}
