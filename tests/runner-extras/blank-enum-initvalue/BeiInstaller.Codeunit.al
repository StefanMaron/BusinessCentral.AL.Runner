// Faithful to the issue #1674 repro: the failing Insert happened inside a
// Subtype=Install trigger (MutableRecordBuffer.MarkAllFieldsAsChanged on the
// install path), so the whole bundle EXEC-FAILed before a single test ran.
codeunit 64204 "BEI Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        BeiSetup: Record "BEI Setup";
    begin
        BeiSetup.Init();
        BeiSetup.Insert(false);
    end;
}
