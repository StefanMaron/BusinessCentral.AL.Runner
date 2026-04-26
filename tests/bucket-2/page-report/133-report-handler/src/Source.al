report 56360 "Test Report Handler"
{
    Caption = 'Test Report Handler';

    dataset
    {
    }

    requestpage
    {
    }
}

codeunit 56360 "Report Handler Logic"
{
    procedure RunReport()
    begin
        Report.Run(56360);
    end;

    procedure RunReportModal()
    begin
        Report.RunModal(56360);
    end;

    procedure RunReportVar()
    var
        rep: Report "Test Report Handler";
    begin
        rep.Run();
    end;

    procedure RunReportVarModal()
    var
        rep: Report "Test Report Handler";
    begin
        rep.RunModal();
    end;

    procedure RunReportVarUseRequestPage()
    var
        rep: Report "Test Report Handler";
    begin
        rep.UseRequestPage(false);
        rep.Run();
    end;
}
