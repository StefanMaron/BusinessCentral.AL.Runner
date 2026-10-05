/// <summary>#5339: the library's only source file does not compile, so the bundle emits nothing at all.</summary>
codeunit 72222 "MOD Uncompiled Codeunit"
{
    trigger OnRun()
    begin
        NoSuchProcedure();
    end;
}
