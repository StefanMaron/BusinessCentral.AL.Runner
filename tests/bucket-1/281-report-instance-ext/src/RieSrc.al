/// Source objects for ReportInstance extended method tests (issue #948).
table 128000 "RIE Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

/// Report that exercises CurrReport.* methods requiring injection or compilation.
/// All the if-false blocks prove these compile; the trigger runs normally.
report 128000 "RIE Report"
{
    dataset
    {
        dataitem(RieData; "RIE Table")
        {
            trigger OnPreDataItem()
            var
                PageNum: Integer;
                Caused: Integer;
                Show: Boolean;
            begin
                if false then begin
                    CurrReport.NewPage();
                    CurrReport.NewPagePerRecord := true;
                    PageNum := CurrReport.PageNo;
                    CurrReport.PaperSource(1, 1);
                    Show := CurrReport.ShowOutput;
                    Caused := CurrReport.TotalsCausedBy;
                end;
            end;
        }
    }
}

/// Exercises ReportInstance methods accessible from external code.
codeunit 128001 "RIE Source"
{
    procedure Execute_NoOp()
    begin
        Report.Execute(128000, '');
    end;

    procedure Print_NoOp()
    var
        Rep: Report "RIE Report";
    begin
        Rep.UseRequestPage(false);
        Rep.Print('');
    end;

    procedure SaveAs_NoOp()
    var
        Rep: Report "RIE Report";
        OutStr: OutStream;
    begin
        Rep.UseRequestPage(false);
        Rep.SaveAs('', ReportFormat::Pdf, OutStr);
    end;

    procedure NewPagePerRecord_Set()
    var
        Rep: Report "RIE Report";
    begin
        Rep.UseRequestPage(false);
        Rep.NewPagePerRecord := true;
    end;

    procedure ValidateAndPrepareLayout_NoOp()
    var
        InStrIn: InStream;
        InStrOut: InStream;
    begin
        Report.ValidateAndPrepareLayout(128000, InStrIn, InStrOut, ReportLayoutType::RDLC);
    end;

    procedure RunWithCurrReportMethods_NoThrow()
    var
        Rep: Report "RIE Report";
    begin
        // Runs the report to exercise CurrReport.* methods in the trigger
        Rep.UseRequestPage(false);
        Rep.Run();
    end;
}
