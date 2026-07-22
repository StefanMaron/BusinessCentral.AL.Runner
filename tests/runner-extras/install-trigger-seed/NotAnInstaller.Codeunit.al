// Negative-direction control: a NORMAL (non-Install-subtype) codeunit with a
// public procedure that happens to be named like an install trigger. If the
// runner's install step matched by method NAME instead of by codeunit
// Subtype=Install, this would insert a 'ROGUE' row — the tests assert it did
// NOT run (row absent, total count stays exactly 3).
codeunit 60712 "ITS Not An Installer"
{
    procedure OnInstallAppPerCompany()
    var
        Seed: Record "Install Seed";
    begin
        Seed.Init();
        Seed."Code" := 'ROGUE';
        Seed."Value" := -1;
        Seed.Insert();
    end;
}
