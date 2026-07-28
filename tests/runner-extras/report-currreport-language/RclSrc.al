table 62020 "RCL Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; LanguageId; Integer) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

/// A report that switches language per row, the way the Base App's document reports do
/// (Standard Sales - Invoice sets CurrReport.Language from the customer's language code in
/// its Header data item's OnAfterGetRecord).
report 62020 "RCL Language Report"
{
    Caption = 'RCL Language Report';
    UsageCategory = ReportsAndAnalysis;
    ApplicationArea = All;
    DefaultRenderingLayout = RclLayout;

    dataset
    {
        dataitem(Rows; "RCL Row")
        {
            column(EntryNo; "Entry No.") { }
            column(LangId; LanguageId) { }

            trigger OnAfterGetRecord()
            begin
                CurrReport.Language := Rows.LanguageId;
                LanguageAfterSet := CurrReport.Language;
                RowsSeen += 1;
            end;
        }
    }

    rendering
    {
        layout(RclLayout)
        {
            Type = RDLC;
            LayoutFile = './RclLayout.rdl';
            Caption = 'RCL layout';
        }
    }

    var
        RowsSeen: Integer;
        LanguageAfterSet: Integer;
        FormatRegionAfterSet: Text;

    trigger OnPreReport()
    begin
        CurrReport.FormatRegion := 'en-US';
        FormatRegionAfterSet := CurrReport.FormatRegion;
    end;

    procedure RowsProcessed(): Integer
    begin
        exit(RowsSeen);
    end;

    procedure LanguageSeen(): Integer
    begin
        exit(LanguageAfterSet);
    end;

    procedure FormatRegionSeen(): Text
    begin
        exit(FormatRegionAfterSet);
    end;
}
