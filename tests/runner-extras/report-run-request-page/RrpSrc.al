/// Backing table with real stored rows — the request page filters over it, so the
/// filter the handler sets is observable in the returned parameters XML.
table 62010 "RRP Row"
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

/// Execution log — a table write proves the handler body ran regardless of which
/// report instance the request page was attached to.
table 62011 "RRP Log"
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
        LogRec: Record "RRP Log";
        NextNo: Integer;
    begin
        if LogRec.FindLast() then
            NextNo := LogRec."Entry No.";
        LogRec.Init();
        LogRec."Entry No." := NextNo + 1;
        LogRec.Marker := NewMarker;
        LogRec.Insert();
    end;

    procedure MarkerCount(WantedMarker: Text[50]): Integer
    var
        LogRec: Record "RRP Log";
    begin
        LogRec.SetRange(Marker, WantedMarker);
        exit(LogRec.Count());
    end;
}

/// A report with a real request page carrying one editable field. ProcessingOnly so
/// nothing about rendering can influence the result — the whole claim is about the
/// request page being routed to its handler.
report 62010 "RRP Request Page Report"
{
    Caption = 'RRP Request Page Report';
    UsageCategory = ReportsAndAnalysis;
    ApplicationArea = All;
    ProcessingOnly = true;

    dataset
    {
        dataitem(Rows; "RRP Row")
        {
            trigger OnAfterGetRecord()
            begin
                RowCount += 1;
            end;
        }
    }

    requestpage
    {
        layout
        {
            area(Content)
            {
                group(Options)
                {
                    field(EchoText; EchoText)
                    {
                        ApplicationArea = All;
                        Caption = 'Echo Text';
                        ToolTip = 'Value the handler sets, echoed back to the test.';
                    }
                }
            }
        }

        trigger OnOpenPage()
        var
            LogRec: Record "RRP Log";
        begin
            LogRec.Log('rp-open');
        end;
    }

    var
        RowCount: Integer;
        EchoText: Text[50];

    procedure GetEchoText(): Text[50]
    begin
        exit(EchoText);
    end;

    procedure RowsProcessed(): Integer
    begin
        exit(RowCount);
    end;
}

/// Same request page, but a report that produces a DATASET — so the parameters a handler
/// produced can be replayed through Report.SaveAs(id, paramsXml, Xml, stream) and the
/// filter's effect observed in the output. That replay is the documented way to run a
/// report headlessly with filters a user chose earlier, and it is what a caller does with
/// what RunRequestPage handed back.
report 62011 "RRP Dataset Report"
{
    Caption = 'RRP Dataset Report';
    UsageCategory = ReportsAndAnalysis;
    ApplicationArea = All;
    DefaultRenderingLayout = RrpLayout;

    dataset
    {
        dataitem(Rows; "RRP Row")
        {
            column(EntryNo; "Entry No.") { }
            column(RowName; Name) { }
        }
    }

    rendering
    {
        layout(RrpLayout)
        {
            Type = RDLC;
            LayoutFile = './RrpLayout.rdl';
            Caption = 'RRP layout';
        }
    }

    requestpage
    {
        layout
        {
            area(Content)
            {
                group(Options)
                {
                    field(EchoText; EchoText)
                    {
                        ApplicationArea = All;
                        Caption = 'Echo Text';
                        ToolTip = 'Unused; present so the request page has a control.';
                    }
                }
            }
        }
    }

    var
        EchoText: Text[50];
}
