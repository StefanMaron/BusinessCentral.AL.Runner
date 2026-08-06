// The Subtype=Install codeunit under test — the exact reproducer pattern from issue
// #1651: any real ISV app with a standard upgrade-tag install pattern reaches
// NavTextConstant.get_Value() through System App's Codeunit 9996
// (SetUpgradeTagForCompany -> AddDefaultTelemetryParameters), which lazily
// initialises Microsoft.Dynamics.Nav.Types.WindowsLanguageHelper. On Linux that
// type's kernel32 P/Invoke is redirected through Win32Stubs; if the shim can't be
// built the whole install trigger used to die with a DllNotFoundException hundreds
// of frames removed from Win32Stubs, and the runner reported a bare suite error
// with 0 tests run — see Win32StubsLoudFailureTests.cs (AlRunner.Tests) for the
// C#-level proof of the loud-failure fix, and this suite for the AL-level proof
// that the trigger, and everything downstream of it, actually runs.
codeunit 62051 "ITTC Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        UpgradeTag: Codeunit "Upgrade Tag";
    begin
        UpgradeTag.SetUpgradeTag(GetReproTag());
    end;

    procedure GetReproTag(): Code[250]
    begin
        exit('ITTC-TEXTCONST-1.0');
    end;
}
