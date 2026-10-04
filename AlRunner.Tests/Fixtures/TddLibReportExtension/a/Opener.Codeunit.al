// #5322: a library that runs a report of its own BY NAME, so nothing in it can start code of a later bundle except through
// a report extension that bundle declares: the extension is the only thing that makes the edges of this library readable.
codeunit 73080 "TRE Opener"
{
    procedure RunOwnReport()
    begin
        Report.Run(Report::"TRE A Report");
    end;
}

report 73081 "TRE A Report"
{
    ProcessingOnly = true;
    UseRequestPage = false;
    trigger OnPreReport() begin end;
}
