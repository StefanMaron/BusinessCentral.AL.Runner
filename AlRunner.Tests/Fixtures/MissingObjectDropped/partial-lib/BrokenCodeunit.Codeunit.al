/// <summary>#5339: calls a procedure that does not exist, so the compile drops this codeunit.</summary>
codeunit 72212 "MOD Dropped Codeunit"
{
    trigger OnRun()
    begin
        NoSuchProcedure();
    end;
}
