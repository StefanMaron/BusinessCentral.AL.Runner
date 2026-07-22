// The Subtype=Install codeunit under test. Real BC fires its lifecycle
// triggers on app install; the runner must fire them once per bundle before
// tests run. Each trigger inserts distinctly-marked rows so the tests can
// prove BOTH triggers fired (not just one).
codeunit 60711 "ITS Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerDatabase()
    var
        Seed: Record "Install Seed";
    begin
        Seed.Init();
        Seed."Code" := 'DATABASE';
        Seed."Value" := 99;
        Seed.Insert();
    end;

    trigger OnInstallAppPerCompany()
    var
        Seed: Record "Install Seed";
    begin
        Seed.Init();
        Seed."Code" := 'COMPANY1';
        Seed."Value" := 11;
        Seed.Insert();

        Seed.Init();
        Seed."Code" := 'COMPANY2';
        Seed."Value" := 22;
        Seed.Insert();
    end;
}
