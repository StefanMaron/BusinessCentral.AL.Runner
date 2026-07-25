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

/// Execution log. Instance globals are only visible if the report ran on the SAME
/// instance the caller holds; a table write is observable regardless of which instance
/// executed, which separates "did not execute" from "executed on another instance".
table 61891 "RRE Log"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Marker; Text[50]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }

    procedure Log(NewMarker: Text[50])
    var
        Log: Record "RRE Log";
        NextNo: Integer;
    begin
        if Log.FindLast() then
            NextNo := Log."Entry No.";
        Log.Init();
        Log."Entry No." := NextNo + 1;
        Log.Marker := NewMarker;
        Log.Insert();
    end;
}

/// Shape A — ProcessingOnly, no rendering layout. The simplest possible report.
report 61890 "RRE ProcessingOnly Report"
{
    Caption = 'RRE ProcessingOnly Report';
    UsageCategory = ReportsAndAnalysis;
    ApplicationArea = All;
    ProcessingOnly = true;

    dataset
    {
        dataitem(Rows; "RRE Row")
        {
            trigger OnAfterGetRecord()
            var
                LogRec: Record "RRE Log";
            begin
                RowCount += 1;
                LogRec.Log('A-row');
            end;
        }
    }

    var
        RowCount: Integer;
        PreReportRan: Boolean;
        PostReportRan: Boolean;

    trigger OnPreReport()
    var
        LogRec: Record "RRE Log";
    begin
        PreReportRan := true;
        LogRec.Log('A-pre');
    end;

    trigger OnPostReport()
    begin
        PostReportRan := true;
    end;

    procedure RowsProcessed(): Integer
    begin
        exit(RowCount);
    end;

    procedure DidPreReportRun(): Boolean
    begin
        exit(PreReportRan);
    end;

    procedure DidPostReportRun(): Boolean
    begin
        exit(PostReportRan);
    end;
}

/// Shape B — a normal (non-ProcessingOnly) report WITH a rendering layout, so the
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
