codeunit 71923 "SIL Install"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        Single: Codeunit "SIL Single";
    begin
        Single.MarkInstall();
    end;
}
