/// Backing table with real stored rows, so the control experiment does not depend on
/// any virtual-table provider.
table 61890 "RRE Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Name; Text[50]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

/// A normal (non-ProcessingOnly) report WITH a rendering layout — the runner-specific
/// difference between "needs a layout" and "never executes" is separable from shape A.
report 61891 "RRE Layout Report"
{
    Caption = 'RRE Layout Report';
    UsageCategory = ReportsAndAnalysis;
    ApplicationArea = All;
    DefaultRenderingLayout = RreWordish;

    dataset
    {
        dataitem(Rows; "RRE Row")
        {
            column(EntryNo; "Entry No.") { }
            column(RowName; Name) { }

            trigger OnAfterGetRecord()
            begin
                RowCount += 1;
            end;
        }
    }

    rendering
    {
        layout(RreWordish)
        {
            Type = RDLC;
            LayoutFile = './RreLayout.rdl';
            Caption = 'RRE layout';
        }
    }

    var
        RowCount: Integer;
        PreReportRan: Boolean;

    trigger OnPreReport()
    begin
        PreReportRan := true;
    end;

    procedure RowsProcessed(): Integer
    begin
        exit(RowCount);
    end;

    procedure DidPreReportRun(): Boolean
    begin
        exit(PreReportRan);
    end;
}
