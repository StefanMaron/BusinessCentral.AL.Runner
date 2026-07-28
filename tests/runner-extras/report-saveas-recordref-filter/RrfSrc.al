table 62030 "RRF Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// A report shaped like a document report: it refuses to run unless its data item carries a
/// filter, the same guard Standard Sales - Invoice uses ("You must specify one or more
/// filters to avoid accidentally printing all documents"). That guard is what turns a
/// dropped record filter from a silently-too-wide dataset into an outright refusal.
report 62030 "RRF Document Report"
{
    Caption = 'RRF Document Report';
    UsageCategory = ReportsAndAnalysis;
    ApplicationArea = All;
    DefaultRenderingLayout = RrfLayout;

    dataset
    {
        dataitem(Rows; "RRF Row")
        {
            column(RowNo; "No.") { }
            column(RowName; Name) { }

            trigger OnAfterGetRecord()
            begin
                RowsSeen += 1;
            end;
        }
    }

    rendering
    {
        layout(RrfLayout)
        {
            Type = RDLC;
            LayoutFile = './RrfLayout.rdl';
            Caption = 'RRF layout';
        }
    }

    var
        RowsSeen: Integer;

    trigger OnPreReport()
    begin
        if Rows.GetFilters() = '' then
            Error(NoFilterErr);
    end;

    var
        NoFilterErr: Label 'You must specify one or more filters to avoid accidentally printing all documents.';

    procedure RowsProcessed(): Integer
    begin
        exit(RowsSeen);
    end;
}
